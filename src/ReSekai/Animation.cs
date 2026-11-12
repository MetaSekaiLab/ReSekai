namespace ReSekai;

static class Animation
{
    internal sealed class Key(float time, float value)
    {
        public float Time = time, Number = value, In, Out;
    }
    internal sealed record Curve(Value Binding, List<Key>[] Channels);
    sealed record Segment(int Frame, float Time, float A, float B, float C, float D);
    static bool Transform(Value binding) => binding["typeID"].Number is 4 or 224 && binding["attribute"].Number is >= 1 and <= 4;

    public static List<Curve> Decode(Value clip)
    {
        var bindings = clip["m_ClipBindingConstant"]["genericBindings"].Items;
        List<Curve> curves = bindings.Select(b => new Curve(b,
            Enumerable.Range(0, Transform(b) ? b["attribute"].Number == 2 ? 4 : 3 : 1).Select(_ => new List<Key>()).ToArray())).ToList();
        var channels = curves.SelectMany(c => c.Channels).ToArray();
        bool[] pointers = curves.SelectMany(c => c.Channels.Select(_ => c.Binding["isPPtrCurve"].Number != 0)).ToArray();
        var muscle = clip["m_MuscleClip"];
        var data = muscle["m_Clip"]["data"];
        var stream = data["m_StreamedClip"];
        var dense = data["m_DenseClip"];
        int streamedCount = checked((int)(stream["curveCount"].Number + (stream.Find("discreteCurveCount")?.Number ?? 0)));
        int denseCount = checked((int)dense["m_CurveCount"].Number), frameCount = checked((int)dense["m_FrameCount"].Number);
        var constants = data["m_ConstantClip"]["data"].Elements;
        if (streamedCount + denseCount + constants.Length != channels.Length) throw new InvalidDataException("动画通道数量与绑定不一致");
        List<Segment>[] segments = Enumerable.Range(0, streamedCount).Select(_ => new List<Segment>()).ToArray();
        Binary reader = new(stream["data"].Bytes);
        int frame = 0;
        float lastTime = 0, previousTime = float.NegativeInfinity;
        while (reader.Remaining > 0)
        {
            float time = Float();
            if (float.IsNaN(time) || time < previousTime) throw new InvalidDataException("动画帧时间顺序无效");
            lastTime = previousTime; previousTime = time;
            int count = reader.Count(20);
            for (int i = 0; i < count; i++)
            {
                int channel = reader.Int();
                if ((uint)channel >= segments.Length) throw new InvalidDataException("动画通道序号越界");
                var entry = new Segment(frame, time, Float(), Float(), Float(), Float());
                var list = segments[channel];
                if (list.Count > 0 && list[^1].Frame == frame) list[^1] = entry;
                else list.Add(entry);
            }
            frame++;
        }
        for (int channel = 0; channel < segments.Length; channel++)
        {
            var source = segments[channel];
            var keys = source.Select(s => new Key(s.Frame == 0 ? 0 : s.Time, s.D)).ToArray();
            double left = source.Count > 0 ? source[0].D : 0;
            for (int i = 0; i < source.Count; i++)
            {
                var s = source[i]; var key = keys[i];
                if (s.Frame == frame - 1) continue;
                if (s.Frame == 0 && !pointers[channel]) { left = i + 1 < source.Count ? source[i + 1].D : 0; continue; }
                if (!pointers[channel] && i + 1 < source.Count && s.Frame < frame - 2)
                {
                    var next = source[i + 1];
                    if (s.A != 0 || s.B != 0 || s.C != 0)
                    {
                        double dt = (double)next.Time - s.Time;
                        key.Out = s.C;
                        keys[i + 1].In = (float)((3 * s.A * dt + 2 * s.B) * dt + s.C);
                        left = ((s.A * dt + s.B) * dt + s.C) * dt + s.D;
                    }
                    else
                    {
                        bool jump = Math.Abs(s.D - left) > 5e-7 * Math.Max(Math.Abs(left), Math.Abs(s.D));
                        key.Out = jump ? float.NegativeInfinity : s.D != next.D ? float.PositiveInfinity : 0;
                        if (jump) key.Number = (float)left;
                        left = s.D;
                    }
                }
                channels[channel].Add(key);
            }
        }
        float rate = dense["m_SampleRate"].Float, begin = dense["m_BeginTime"].Float, stop = muscle["m_StopTime"].Float;
        var samples = dense["m_SampleArray"].Elements;
        if (frameCount < 0 || denseCount < 0 || (long)frameCount * denseCount != samples.Length || denseCount > 0 && (!(rate > 0) || !float.IsFinite(rate)))
            throw new InvalidDataException("动画等距采样数据无效");
        for (int c = 0; c < denseCount; c++)
        {
            var keys = channels[streamedCount + c];
            for (int f = 0; f < frameCount; f++)
            {
                float time = begin + f / rate, value = samples[f * denseCount + c].Float;
                // Dense 末尾可含插值用的额外采样，不能据此延长片段。
                if (time > stop)
                {
                    if (keys.Count > 0 && keys[^1].Time < stop)
                    {
                        var previous = keys[^1];
                        keys.Add(new(stop, previous.Number + (value - previous.Number) * ((stop - previous.Time) / (time - previous.Time))));
                    }
                    break;
                }
                keys.Add(new(time, value));
            }
            for (int f = 1; f < keys.Count; f++) keys[f].In = keys[f - 1].Out = (keys[f].Number - keys[f - 1].Number) / (keys[f].Time - keys[f - 1].Time);
        }
        float clipRate = clip["m_SampleRate"].Float;
        if (frame > 1 && MathF.Round(stop * clipRate) - MathF.Round(Math.Max(0, lastTime) * clipRate) == 1 &&
            segments.Where((s, i) => pointers[i]).Any(s => s.Any(k => k.Frame == frame - 2))) stop = Math.Max(0, lastTime);
        for (int i = 0; i < constants.Length; i++)
        {
            var keys = channels[streamedCount + denseCount + i];
            keys.Add(new(0, constants[i].Float));
            if (stop > 0) keys.Add(new(stop, constants[i].Float));
        }
        return curves;
        float Float() => BitConverter.Int32BitsToSingle(reader.Int());
    }

    public static Value Convert(Value clip, Schema editor, AnimationNames.Resolver names)
    {
        Dictionary<string, Value> fields = clip.Items.ToDictionary(v => v.Schema.Name);
        void Set(string name, object value) => fields[name] = new(Child(editor, name), value);
        var settings = Child(editor, "m_AnimationClipSettings");
        var muscle = clip["m_MuscleClip"];
        Set(settings.Name, settings.Children.Where(s => muscle.Find(s.Name) is not null).Select(s => new Value(s, muscle[s.Name].Data)).ToArray());
        if (clip["m_Legacy"].Number != 0) return new(editor, fields.Values.ToArray());

        Dictionary<string, List<Value>> groups = new()
        {
            ["m_PositionCurves"] = [], ["m_RotationCurves"] = [], ["m_ScaleCurves"] = [],
            ["m_EulerCurves"] = [], ["m_FloatCurves"] = [], ["m_PPtrCurves"] = []
        };
        var mapping = clip["m_ClipBindingConstant"]["pptrCurveMapping"].Items;
        foreach (var curve in Decode(clip))
        {
            Value b = curve.Binding;
            bool transform = Transform(b), pointer = b["isPPtrCurve"].Number != 0;
            string field = transform ? b["attribute"].Number switch { 1 => "m_PositionCurves", 2 => "m_RotationCurves", 3 => "m_ScaleCurves", _ => "m_EulerCurves" }
                : pointer ? "m_PPtrCurves" : "m_FloatCurves";
            Schema element = Child(editor, field).Element!;
            string path = names.Path((uint)b["path"].Number);
            if (curve.Channels.Any(c => c.Count == 0)) throw new InvalidDataException("动画绑定没有关键帧：" + path);
            object value;
            if (pointer)
            {
                Schema key = Child(element, "curve").Element!;
                value = curve.Channels[0].Select(k =>
                {
                    int index = checked((int)k.Number);
                    if (index != k.Number || (uint)index >= mapping.Length) throw new InvalidDataException("动画对象引用序号越界");
                    return Value.Make(key, ("time", k.Time), ("value", mapping[index]));
                }).ToArray();
            }
            else value = Keyframes(Child(element, "curve"), curve.Channels, transform && b["attribute"].Number == 4 ? (int)b["customType"].Number : 4);
            groups[field].Add(transform ? Value.Make(element, ("path", path), ("curve", value))
                : Value.Make(element, ("path", path), ("curve", value), ("attribute", names.Attribute(b)),
                    ("classID", b["typeID"]), ("script", b["script"]), ("flags", pointer ? 2 : 0)));
        }
        foreach (var group in groups) Set(group.Key, group.Value.ToArray());
        Set("m_Compressed", false);
        return new(editor, fields.Values.ToArray());
    }

    static Value Keyframes(Schema schema, List<Key>[] channels, int order)
    {
        var first = channels[0];
        if (channels.Skip(1).Any(c => !c.Select(k => k.Time).SequenceEqual(first.Select(k => k.Time))))
            throw new NotSupportedException("变换曲线的分量时间不同，尚未重采样");
        Schema keySchema = Child(schema, "m_Curve").Element!;
        var keys = first.Select((key, i) =>
        {
            object Vector(string name, Func<Key, float> get) => channels.Length == 1 ? get(key)
                : Value.Make(Child(keySchema, name), channels.Select((c, d) => ("xyzw"[d].ToString(), (object?)get(c[i]))).ToArray());
            return Value.Make(keySchema, ("time", key.Time), ("value", Vector("value", k => k.Number)),
                ("inSlope", Vector("inSlope", k => k.In)), ("outSlope", Vector("outSlope", k => k.Out)),
                ("tangentMode", 1), ("weightedMode", 0), ("inWeight", Vector("inWeight", _ => 1f / 3)), ("outWeight", Vector("outWeight", _ => 1f / 3)));
        }).ToArray();
        return Value.Make(schema, ("m_Curve", keys), ("m_PreInfinity", 2), ("m_PostInfinity", 2), ("m_RotationOrder", order));
    }
    static Schema Child(Schema schema, string name) => schema.Children.First(c => c.Name == name);

    static readonly string[] muscles = MuscleNames();
    public static string? MuscleName(uint index) => index < muscles.Length ? muscles[index] : null;
    static string[] MuscleNames()
    {
        List<string> names = [];
        foreach (string bone in new[] { "Motion", "Root", "LeftFoot", "RightFoot", "LeftHand", "RightHand" })
        foreach (string channel in new[] { "T.x", "T.y", "T.z", "Q.x", "Q.y", "Q.z", "Q.w" }) names.Add(bone + channel);
        foreach (string bone in new[] { "Spine", "Chest", "UpperChest" })
        foreach (string channel in new[] { "Front-Back", "Left-Right", "Twist Left-Right" }) names.Add(bone + " " + channel);
        foreach (string bone in new[] { "Neck", "Head" })
        foreach (string channel in new[] { "Nod Down-Up", "Tilt Left-Right", "Turn Left-Right" }) names.Add(bone + " " + channel);
        foreach (string side in new[] { "Left", "Right" })
        foreach (string channel in new[] { "Down-Up", "In-Out" }) names.Add(side + " Eye " + channel);
        names.AddRange(["Jaw Close", "Jaw Left-Right"]);
        foreach (string side in new[] { "Left", "Right" })
        foreach (string channel in new[] { "Upper Leg Front-Back", "Upper Leg In-Out", "Upper Leg Twist In-Out", "Lower Leg Stretch", "Lower Leg Twist In-Out", "Foot Up-Down", "Foot Twist In-Out", "Toes Up-Down" }) names.Add(side + " " + channel);
        foreach (string side in new[] { "Left", "Right" })
        foreach (string channel in new[] { "Shoulder Down-Up", "Shoulder Front-Back", "Arm Down-Up", "Arm Front-Back", "Arm Twist In-Out", "Forearm Stretch", "Forearm Twist In-Out", "Hand Down-Up", "Hand In-Out" }) names.Add(side + " " + channel);
        foreach (string hand in new[] { "LeftHand", "RightHand" })
        foreach (string finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
        foreach (string channel in new[] { "1 Stretched", "Spread", "2 Stretched", "3 Stretched" }) names.Add(hand + "." + finger + "." + channel);
        foreach (string bone in new[] { "Spine", "Chest", "UpperChest", "Neck", "Head", "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes", "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes", "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand", "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand" })
        foreach (char axis in "xyz") names.Add(bone + "TDOF." + axis);
        return names.ToArray();
    }
}
