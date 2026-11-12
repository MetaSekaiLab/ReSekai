using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace ReSekai;

sealed record SpritePart(ObjectId Source, Value Value, Value RenderData, long FileId);
sealed record TextureGroup(ObjectId? Source, SpritePart[] Sprites);
sealed record Pixels(int Width, int Height, byte[] Rgba)
{
    public Pixels(int width, int height) : this(width, height, new byte[checked(width * height * 4)]) { }
}

static class Textures
{
    public static Pixels Decode(Value texture, Resources resources)
    {
        byte[] bytes = texture["image data"].Bytes;
        if (bytes.Length == 0) bytes = resources.Read(texture["m_StreamData"]);
        Pixels image = new(checked((int)texture["m_Width"].Number), checked((int)texture["m_Height"].Number));
        Media.Decode(bytes, image.Width, image.Height, checked((int)texture["m_TextureFormat"].Number), image.Rgba);
        return image;
    }

    public static void Write(Document document, Resources resources, Output output)
    {
        var group = document.Texture!;
        Value? settings = group.Source is { } source ? resources.Object(source) : null;
        Pixels image;
        if (settings is not null && (settings["image data"].Bytes.Length > 0 || settings["m_StreamData"]["size"].Number > 0)) image = Decode(settings, resources);
        else
        {
            int width = group.Sprites.Max(s => checked((int)MathF.Ceiling(s.Value["m_Rect"]["x"].Float + s.Value["m_Rect"]["width"].Float)));
            int height = group.Sprites.Max(s => checked((int)MathF.Ceiling(s.Value["m_Rect"]["y"].Float + s.Value["m_Rect"]["height"].Float)));
            image = new(width, height);
            foreach (var page in group.Sprites.GroupBy(s => (ObjectId)s.RenderData["texture"].Data!))
            {
                Value texture = resources.Object(page.Key);
                settings ??= texture;
                Pixels pixels = Decode(texture, resources);
                foreach (var sprite in page) Paint(image, pixels, sprite);
            }
        }
        if (settings is null) throw new InvalidDataException("缺少纹理设置");
        output.Write(document.Path, Png(image));
        output.Write(document.Path + ".meta", $"fileFormatVersion: 2\nguid: {document.Guid}\n" + Metadata(document, image, settings));
    }

    static string Metadata(Document document, Pixels pixels, Value texture)
    {
        var sprites = document.Texture!.Sprites;
        var single = sprites.Length == 1 ? sprites[0].Value : null;
        var first = sprites.FirstOrDefault()?.Value;
        int mode = sprites.Length == 0 ? 0 : single is null ? 2 : 1;
        Value sampler = texture["m_TextureSettings"];
        int maxSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(32, Math.Max(pixels.Width, pixels.Height)));
        Dictionary<string, object?> Pair(Value v, params string[] names) => names.ToDictionary(n => n, n => (object?)v[n].Float);
        var spriteSheet = new Dictionary<string, object?>
        {
            ["serializedVersion"] = 2,
            ["sprites"] = mode != 2 ? Array.Empty<object>() : sprites.Select(s => (object)new Dictionary<string, object?>
            {
                ["serializedVersion"] = 2, ["name"] = s.Value["m_Name"].Text,
                ["rect"] = Pair(s.Value["m_Rect"], "x", "y", "width", "height"), ["alignment"] = 9,
                ["pivot"] = Pair(s.Value["m_Pivot"], "x", "y"), ["border"] = Pair(s.Value["m_Border"], "x", "y", "z", "w"),
                ["spriteID"] = Output.Guid("sprite:" + document.Guid + ":" + s.FileId), ["internalID"] = s.FileId,
                ["outline"] = Array.Empty<object>(), ["physicsShape"] = Array.Empty<object>(), ["bones"] = Array.Empty<object>(),
                ["tessellationDetail"] = -1
            }).ToArray(),
            ["spriteID"] = single is null ? "" : Output.Guid("sprite:" + document.Guid + ":21300000"),
            ["nameFileIdTable"] = mode == 2 ? sprites.ToDictionary(s => s.Value["m_Name"].Text, s => (object?)s.FileId) : new Dictionary<string, object?>()
        };
        var fields = new Dictionary<string, object?>
        {
            ["serializedVersion"] = 13, ["internalIDToNameTable"] = Array.Empty<object>(), ["externalObjects"] = new Dictionary<string, object?>(),
            ["mipmaps"] = new Dictionary<string, object?> { ["enableMipMap"] = texture["m_MipCount"].Number > 1, ["sRGBTexture"] = texture["m_ColorSpace"].Number == 1, ["mipMapMode"] = 0 },
            ["isReadable"] = texture["m_IsReadable"].Number != 0, ["maxTextureSize"] = maxSize, ["nPOTScale"] = 0,
            ["textureSettings"] = new Dictionary<string, object?>
            {
                ["serializedVersion"] = 2, ["filterMode"] = sampler["m_FilterMode"].Number, ["aniso"] = sampler["m_Aniso"].Number,
                ["mipBias"] = sampler["m_MipBias"].Float, ["wrapU"] = sampler["m_WrapU"].Number, ["wrapV"] = sampler["m_WrapV"].Number, ["wrapW"] = sampler["m_WrapW"].Number
            },
            ["spriteMode"] = mode, ["spritePixelsToUnits"] = first?["m_PixelsToUnits"].Float ?? 100f,
            ["spriteExtrude"] = first?["m_Extrude"].Number ?? 1, ["spriteMeshType"] = first is null ? 1 : (first["m_RD"]["settingsRaw"].Number >> 6) & 1,
            ["alignment"] = single is null ? 0 : 9, ["spritePivot"] = single is null ? new Dictionary<string, object?> { ["x"] = .5f, ["y"] = .5f } : Pair(single["m_Pivot"], "x", "y"),
            ["spriteBorder"] = single is null ? new Dictionary<string, object?> { ["x"] = 0, ["y"] = 0, ["z"] = 0, ["w"] = 0 } : Pair(single["m_Border"], "x", "y", "z", "w"),
            ["textureType"] = mode == 0 ? 0 : 8, ["textureShape"] = 1, ["alphaUsage"] = 1, ["alphaIsTransparency"] = mode != 0,
            ["spriteGenerateFallbackPhysicsShape"] = true, ["spriteTessellationDetail"] = -1, ["swizzle"] = 50462976,
            ["spriteSheet"] = spriteSheet, ["userData"] = "", ["assetBundleName"] = document.Bundle, ["assetBundleVariant"] = "",
            ["platformSettings"] = new[] { "DefaultTexturePlatform", "Android" }.Select(platform => (object)new Dictionary<string, object?>
            {
                ["serializedVersion"] = 3, ["buildTarget"] = platform, ["maxTextureSize"] = maxSize,
                ["textureFormat"] = platform == "Android" ? texture["m_TextureFormat"].Number : -1,
                ["textureCompression"] = 1, ["compressionQuality"] = 50, ["overridden"] = platform == "Android"
            }).ToArray()
        };
        return Yaml.Mapping("TextureImporter", fields);
    }

    static void Paint(Pixels target, Pixels page, SpritePart part)
    {
        var sprite = part.Value; var data = part.RenderData;
        var rectangle = sprite["m_Rect"]; var packed = data["textureRect"]; var offset = data["textureRectOffset"];
        int rotation = (int)((data["settingsRaw"].Number >> 2) & 15);
        if (rotation is < 0 or > 4) throw new NotSupportedException("Sprite 打包旋转 " + rotation);
        int x0 = (int)MathF.Round(packed["x"].Float), y0 = (int)MathF.Round(packed["y"].Float);
        int width = (int)MathF.Round(packed["width"].Float), height = (int)MathF.Round(packed["height"].Float);
        int outWidth = rotation == 4 ? height : width, outHeight = rotation == 4 ? width : height;
        int left = (int)MathF.Round(rectangle["x"].Float + offset["x"].Float), bottom = (int)MathF.Round(rectangle["y"].Float + offset["y"].Float);
        var triangles = (data["settingsRaw"].Number & 2) != 0 && ((data["settingsRaw"].Number >> 6) & 1) != 0 ? Triangles(sprite) : null;
        for (int y = 0; y < outHeight; y++)
        for (int x = 0; x < outWidth; x++)
        {
            if (triangles is not null && !Covered(triangles, new(x + offset["x"].Float, y + offset["y"].Float))) continue;
            (int px, int py) = rotation switch { 1 => (width - 1 - x, y), 2 => (x, height - 1 - y), 3 => (width - 1 - x, height - 1 - y), 4 => (y, height - 1 - x), _ => (x, y) };
            px += x0; py += y0;
            if ((uint)px >= page.Width || (uint)py >= page.Height || (uint)(left + x) >= target.Width || (uint)(bottom + y) >= target.Height) throw new InvalidDataException("Sprite 像素超出纹理边界");
            page.Rgba.AsSpan((py * page.Width + px) * 4, 4).CopyTo(target.Rgba.AsSpan(((bottom + y) * target.Width + left + x) * 4, 4));
        }
    }

    static Vector2[][] Triangles(Value sprite)
    {
        var data = sprite["m_RD"]; var vertex = data["m_VertexData"];
        var channels = vertex["m_Channels"].Items.Where(c => c["stream"].Number == 0 && (c["dimension"].Number & 15) != 0).ToArray();
        if (channels.Length == 0 || channels[0]["format"].Number != 0) throw new NotSupportedException("Sprite 顶点位置不是 float");
        int stride = channels.Max(c => checked((int)(c["offset"].Number + (c["dimension"].Number & 15) * (c["format"].Number switch { 0 or 10 or 11 => 4, 1 or 4 or 5 or 8 or 9 => 2, _ => 1 }))));
        var indices = data["m_IndexBuffer"].Bytes; var bytes = vertex["m_DataSize"].Bytes;
        Vector2 Point(int i)
        {
            int at = BinaryPrimitives.ReadUInt16LittleEndian(indices.AsSpan(i * 2)) * stride + (int)channels[0]["offset"].Number;
            return new Vector2(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4)) * sprite["m_PixelsToUnits"].Float
                + new Vector2(sprite["m_Pivot"]["x"].Float * sprite["m_Rect"]["width"].Float, sprite["m_Pivot"]["y"].Float * sprite["m_Rect"]["height"].Float);
        }
        return Enumerable.Range(0, indices.Length / 6).Select(i => new[] { Point(i * 3), Point(i * 3 + 1), Point(i * 3 + 2) }).ToArray();
    }
    static bool Covered(Vector2[][] triangles, Vector2 pixel)
    {
        foreach (var triangle in triangles)
        {
            for (int sample = 0; sample < 5; sample++)
            {
                var point = pixel + (sample switch { 0 => new Vector2(.5f), 1 => Vector2.Zero, 2 => Vector2.UnitX, 3 => Vector2.UnitY, _ => Vector2.One });
                bool positive = false, negative = false;
                for (int edge = 0; edge < 3; edge++)
                {
                    var a = triangle[(edge + 1) % 3] - triangle[edge]; var b = point - triangle[edge];
                    float cross = a.X * b.Y - a.Y * b.X;
                    positive |= cross > 0; negative |= cross < 0;
                    if (positive && negative) break;
                }
                if (!positive || !negative) return true;
            }
        }
        return false;
    }

    public static byte[] Png(Pixels image)
    {
        using MemoryStream output = new();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string name, byte[] bytes)
        {
            byte[] type = Encoding.ASCII.GetBytes(name);
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); output.Write(number); output.Write(type); output.Write(bytes);
            uint crc = uint.MaxValue;
            foreach (byte b in type.Concat(bytes)) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320); }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
        }
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height); header[8] = 8; header[9] = 6;
        Chunk("IHDR", header);
        using MemoryStream packed = new();
        using (ZLibStream zip = new(packed, CompressionLevel.Optimal, true))
        {
            int stride = image.Width * 4; byte[] row = new byte[stride + 1]; row[0] = 2;
            for (int y = image.Height - 1; y >= 0; y--)
            {
                for (int x = 0; x < stride; x++) row[x + 1] = unchecked((byte)(image.Rgba[y * stride + x] - (y + 1 < image.Height ? image.Rgba[(y + 1) * stride + x] : 0)));
                zip.Write(row);
            }
        }
        Chunk("IDAT", packed.ToArray()); Chunk("IEND", []);
        return output.ToArray();
    }
}
