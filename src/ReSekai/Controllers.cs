namespace ReSekai;

sealed record EditorObject(int ClassId, long Id, Value Value);

sealed class Controllers(Value input, Document document, Action<string> issue)
{
    readonly Dictionary<uint, string> names = input["m_TOS"].Items.ToDictionary(v => checked((uint)v.Items[0].Number), v => v.Items[1].Text);
    readonly Dictionary<long, EditorObject> objects = [];
    static Schema Layout(int cls) => Schemas.Classes[cls].Editor!;
    static Schema Field(Schema schema, string name) => schema.Children.First(c => c.Name == name);
    static Value[] Data(Value value) => value.Elements.Select(v => v.Schema.Type == "OffsetPtr" ? v["data"] : v).ToArray();
    static Value Ref(long id) => new(new("PPtr<Object>", "reference"), new LocalReference(id));
    long Id(string key) => System.Convert.ToInt64(Output.Guid("controller:" + document.Guid + ":" + key)[..15], 16);
    string Name(long id)
    {
        if (names.TryGetValue(checked((uint)id), out string? name)) return name;
        if (id != 0 && id != uint.MaxValue) issue($"名称哈希未解析：{id}");
        return "";
    }
    Value Add(int cls, string key, params (string Name, object? Data)[] fields)
    {
        long id = Id(key);
        if (id == document.Main || objects.ContainsKey(id)) throw new InvalidDataException("控制器对象标识重复：" + key);
        var value = Value.Make(Layout(cls), [("m_ObjectHideFlags", 1), .. fields]);
        objects[id] = new(cls, id, value);
        return Ref(id);
    }
    static Value Position(Schema parent, string field, float x, float y) => Value.Make(Field(parent, field), ("x", x), ("y", y), ("z", 0f));

    public IReadOnlyList<EditorObject> Convert()
    {
        var constant = input["m_Controller"];
        var defaults = constant["m_DefaultValues"]["data"];
        var root = Layout(91);
        var parameterLayout = Field(root, "m_AnimatorParameters").Element!;
        var parameters = Data(constant["m_Values"]["data"]["m_ValueArray"]).Select(p =>
        {
            int type = (int)p["m_Type"].Number, index = (int)p["m_Index"].Number;
            object? Default(string name) => defaults[name].Elements[index].Data;
            return Value.Make(parameterLayout, ("m_Name", Name(p["m_ID"].Number)), ("m_Type", type),
                ("m_DefaultFloat", type == 1 ? Default("m_FloatValues") : 0f), ("m_DefaultInt", type == 3 ? Default("m_IntValues") : 0),
                ("m_DefaultBool", type is 4 or 9 ? Default("m_BoolValues") : false), ("m_Controller", Ref(document.Main)));
        }).ToArray();
        var layerLayout = Field(root, "m_AnimatorLayers").Element!;
        var constants = Data(constant["m_LayerArray"]);
        var machines = Data(constant["m_StateMachineArray"]);
        List<Value> layers = [];
        Dictionary<long, int> firstLayer = [];
        for (int i = 0; i < constants.Length; i++)
        {
            var layer = constants[i]; long machine = layer["m_StateMachineIndex"].Number;
            string name = Name(layer["m_Binding"].Number);
            bool synced = firstLayer.TryGetValue(machine, out int original);
            if (!synced) firstLayer[machine] = i;
            Value stateMachine = synced ? Ref(0) : Machine(machines[checked((int)machine)], name, i);
            layers.Add(Value.Make(layerLayout, ("m_Name", name), ("m_StateMachine", stateMachine),
                ("m_BlendingMode", layer.Items.First(v => v.Schema.Name.EndsWith("m_LayerBlendingMode", StringComparison.Ordinal))),
                ("m_SyncedLayerIndex", synced ? original : -1), ("m_DefaultWeight", layer["m_DefaultWeight"]), ("m_IKPass", layer["m_IKPass"]),
                ("m_SyncedLayerAffectsTiming", layer["m_SyncedLayerAffectsTiming"]), ("m_Controller", Ref(document.Main))));
        }
        return [new(91, document.Main, Value.Make(root, ("m_Name", input["m_Name"]), ("m_AnimatorParameters", parameters), ("m_AnimatorLayers", layers.ToArray()))),
            .. objects.Values.OrderBy(o => o.Id)];
    }

    Value Machine(Value machine, string layer, int index)
    {
        var states = Data(machine["m_StateConstantArray"]);
        var selectors = Data(machine["m_SelectorStateConstantArray"]);
        string StateKey(int state) => $"{index}/state/{state}";
        string MachineKey(string path) => $"{index}/machine/{path}";
        string Parent(string path) => path.Contains('.') ? path[..path.LastIndexOf('.')] : layer;
        string[] parents = states.Select(s =>
        {
            string full = Name(s["m_FullPathID"].Number), name = Name(s["m_NameID"].Number);
            return name.Length > 0 && full.EndsWith("." + name, StringComparison.Ordinal) ? full[..^(name.Length + 1)] : layer;
        }).ToArray();
        string[] selectorPaths = selectors.Select(s => Name(s["m_FullPathID"].Number)).ToArray();
        SortedSet<string> paths = new(StringComparer.Ordinal) { layer };
        foreach (string candidate in parents.Concat(selectorPaths))
            for (string p = candidate; p.StartsWith(layer + ".", StringComparison.Ordinal); p = Parent(p)) paths.Add(p);

        (Value State, Value Machine, bool Exit) Destination(long destination)
        {
            if (destination < states.Length) return (Ref(Id(StateKey((int)destination))), Ref(0), false);
            int selector = destination >= 30000 && destination < 30000 + selectors.Length ? (int)(destination - 30000) : -1;
            if (selector >= 0 && selector < selectors.Length)
            {
                if (selectors[selector]["m_IsEntry"].Number == 0) return (Ref(0), Ref(0), true);
                if (paths.Contains(selectorPaths[selector])) return (Ref(0), Ref(Id(MachineKey(selectorPaths[selector]))), false);
            }
            issue($"转换目标未解析：{destination}");
            return (Ref(0), Ref(0), false);
        }
        Value Transition(Value source, string key, bool stateTransition)
        {
            int cls = stateTransition ? 1101 : 1109;
            var (state, targetMachine, exit) = Destination(source[stateTransition ? "m_DestinationState" : "m_Destination"].Number);
            Schema condition = Field(Layout(cls), "m_Conditions").Element!;
            var conditions = Data(source["m_ConditionConstantArray"]).Where(c => c["m_ConditionMode"].Number != 5).Select(c => Value.Make(condition,
                ("m_ConditionMode", c["m_ConditionMode"]), ("m_ConditionEvent", Name(c["m_EventID"].Number)), ("m_EventTreshold", c["m_EventThreshold"]))).ToArray();
            List<(string, object?)> fields = [("m_Name", stateTransition ? Name(source["m_UserID"].Number) : ""), ("m_Conditions", conditions),
                ("m_DstState", state), ("m_DstStateMachine", targetMachine), ("m_IsExit", exit)];
            if (stateTransition)
                foreach (string field in new[] { "m_TransitionDuration", "m_TransitionOffset", "m_ExitTime", "m_HasExitTime", "m_HasFixedDuration", "m_InterruptionSource", "m_OrderedInterruption", "m_CanTransitionToSelf" }) fields.Add((field, source[field]));
            return Add(cls, key, fields.ToArray());
        }
        for (int i = 0; i < states.Length; i++)
        {
            var state = states[i]; string key = StateKey(i);
            List<(string, object?)> fields = [("m_Name", Name(state["m_NameID"].Number)), ("m_Tag", Name(state["m_TagID"].Number)),
                ("m_Transitions", Data(state["m_TransitionConstantArray"]).Select((t, j) => Transition(t, key + "/transition/" + j, true)).ToArray())];
            foreach (string field in new[] { "m_Speed", "m_CycleOffset", "m_IKOnFeet", "m_WriteDefaultValues", "m_Mirror" }) fields.Add((field, state[field]));
            foreach (string param in new[] { "Speed", "Mirror", "CycleOffset", "Time" })
            {
                long hash = state["m_" + param + "ParamID"].Number;
                fields.Add(("m_" + param + "ParameterActive", hash != 0)); fields.Add(("m_" + param + "Parameter", Name(hash)));
            }
            var trees = Data(state["m_BlendTreeConstantArray"]);
            fields.Add(("m_Motion", trees.Length == 0 ? Ref(0) : Motion(Data(trees[0]["m_NodeArray"]), 0, key, [])));
            Add(1102, key, fields.ToArray());
        }
        Dictionary<string, List<Value>> entries = [];
        Dictionary<string, long> defaults = [];
        Dictionary<string, List<(string Child, Value Transition)>> exits = [];
        for (int i = 0; i < selectors.Length; i++)
        {
            string path = selectorPaths[i]; if (!paths.Contains(path)) continue;
            var transitions = Data(selectors[i]["m_TransitionConstantArray"]);
            string key = MachineKey(path) + "/selector/" + i;
            if (selectors[i]["m_IsEntry"].Number != 0)
            {
                entries[path] = transitions.SkipLast(1).Select((t, j) => Transition(t, key + "/" + j, false)).ToList();
                if (transitions.Length > 0 && transitions[^1]["m_Destination"].Number is var destination && destination < states.Length) defaults[path] = Id(StateKey((int)destination));
            }
            else if (path != layer)
            {
                string parent = Parent(path);
                if (!exits.TryGetValue(parent, out var list)) exits[parent] = list = [];
                list.AddRange(transitions.Select((t, j) => (path, Transition(t, key + "/" + j, false))));
            }
        }
        if (!defaults.ContainsKey(layer) && machine["m_DefaultState"].Number is var fallback && fallback < states.Length) defaults[layer] = Id(StateKey((int)fallback));
        var any = Data(machine["m_AnyStateTransitionConstantArray"]).Select((t, i) => Transition(t, $"{index}/any/{i}", true)).ToArray();
        Schema layout = Layout(1107), childState = Field(layout, "m_ChildStates").Element!, childMachine = Field(layout, "m_ChildStateMachines").Element!;
        foreach (string path in paths)
        {
            int row = 0;
            Value PositionOf(Schema schema) => Position(schema, "m_Position", 280 + row / 12 * 240, 60 + row++ % 12 * 65);
            var childStates = Enumerable.Range(0, states.Length).Where(i => parents[i] == path).Select(i => Value.Make(childState, ("m_State", Ref(Id(StateKey(i)))), ("m_Position", PositionOf(childState)))).ToArray();
            var childMachines = paths.Where(p => p != layer && Parent(p) == path).Select(p => Value.Make(childMachine, ("m_StateMachine", Ref(Id(MachineKey(p)))), ("m_Position", PositionOf(childMachine)))).ToArray();
            Schema pair = Field(layout, "m_StateMachineTransitions").Element!;
            var transitions = (exits.GetValueOrDefault(path) ?? []).GroupBy(t => t.Child).Select(g => Value.Make(pair, ("first", Ref(Id(MachineKey(g.Key)))), ("second", g.Select(t => t.Transition).ToArray()))).ToArray();
            Add(1107, MachineKey(path), ("m_Name", path == layer ? layer : path[(path.LastIndexOf('.') + 1)..]),
                ("m_ChildStates", childStates), ("m_ChildStateMachines", childMachines), ("m_AnyStateTransitions", path == layer ? any : Array.Empty<Value>()),
                ("m_EntryTransitions", (entries.GetValueOrDefault(path) ?? []).ToArray()), ("m_StateMachineTransitions", transitions),
                ("m_DefaultState", Ref(defaults.GetValueOrDefault(path))), ("m_AnyStatePosition", Position(layout, "m_AnyStatePosition", 40, 30)),
                ("m_EntryPosition", Position(layout, "m_EntryPosition", 40, 120)), ("m_ExitPosition", Position(layout, "m_ExitPosition", 850, 120)),
                ("m_ParentStateMachinePosition", Position(layout, "m_ParentStateMachinePosition", 850, 30)));
        }
        return Ref(Id(MachineKey(layer)));
    }

    Value Motion(Value[] nodes, int index, string owner, HashSet<int> visiting)
    {
        if ((uint)index >= nodes.Length || !visiting.Add(index)) throw new InvalidDataException("BlendTree 子节点越界或形成循环");
        Value node = nodes[index]; var children = node["m_ChildIndices"].Elements;
        if (children.Length == 0)
        {
            visiting.Remove(index);
            long clip = node["m_ClipID"].Number;
            if (clip == uint.MaxValue) return Ref(0);
            if ((ulong)clip >= (ulong)input["m_AnimationClips"].Items.Length) throw new InvalidDataException("BlendTree 动画编号越界");
            return input["m_AnimationClips"].Items[checked((int)clip)];
        }
        string key = owner + "/blend/" + index;
        if (objects.ContainsKey(Id(key))) { visiting.Remove(index); return Ref(Id(key)); }
        var thresholds = node["m_Blend1dData"]["data"]["m_ChildThresholdArray"].Elements;
        var positions = Data(node["m_Blend2dData"]["data"]["m_ChildPositionArray"]);
        var direct = node["m_BlendDirectData"]["data"];
        var events = direct["m_ChildBlendEventIDArray"].Elements;
        Schema childLayout = Field(Layout(206), "m_Childs").Element!;
        var childValues = children.Select((c, i) =>
        {
            int child = checked((int)c.Number);
            var data = nodes[child]; bool leaf = data["m_ChildIndices"].Elements.Length == 0;
            float duration = data["m_Duration"].Float;
            if (leaf && duration <= 0) issue("BlendTree 叶子时长为零，使用默认播放倍率");
            return Value.Make(childLayout, ("m_Motion", Motion(nodes, child, owner, visiting)), ("m_Threshold", i < thresholds.Length ? thresholds[i] : 0f),
                ("m_Position", i < positions.Length ? positions[i] : null), ("m_TimeScale", leaf && duration > 0 ? 1f / duration : 1f),
                ("m_CycleOffset", data["m_CycleOffset"]), ("m_DirectBlendParameter", i < events.Length ? Name(events[i].Number) : ""), ("m_Mirror", leaf && data["m_Mirror"].Number != 0));
        }).ToArray();
        visiting.Remove(index);
        return Add(206, key, ("m_Name", "Blend Tree"), ("m_Childs", childValues), ("m_BlendParameter", Name(node["m_BlendEventID"].Number)),
            ("m_BlendParameterY", Name(node["m_BlendEventYID"].Number)), ("m_MinThreshold", thresholds.Length == 0 ? 0f : thresholds.Min(t => t.Float)),
            ("m_MaxThreshold", thresholds.Length == 0 ? 1f : thresholds.Max(t => t.Float)), ("m_UseAutomaticThresholds", false),
            ("m_NormalizedBlendValues", direct["m_NormalizedBlendValues"]), ("m_BlendType", node["m_BlendType"]));
    }
}
