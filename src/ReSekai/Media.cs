using AssetRipper.TextureDecoder.Astc;
using AssetRipper.TextureDecoder.Dxt;
using AssetRipper.TextureDecoder.Etc;
using AssetRipper.TextureDecoder.Rgb;
using AssetRipper.TextureDecoder.Rgb.Formats;
using Fmod5Sharp;

namespace ReSekai;

static class Media
{
    public static Value Inline(Value value, Resources resources)
    {
        if (value.Find("m_StreamData") is { } stream && stream["size"].Number > 0)
        {
            byte[] bytes = resources.Read(stream);
            value = value.Find("m_VertexData") is { } vertices
                ? value.Set("m_VertexData", vertices.Set("m_DataSize", bytes).Data)
                : value.Set("image data", bytes);
            value = value.Set("m_StreamData", stream.Items.Select(v => v with { Data = v.Schema.Type == "string" ? "" : 0 }).ToArray());
        }
        if (value.Schema.Type is "Texture2D" or "Cubemap" && value.Find("image data") is { Bytes.Length: > 0 } pixels)
        {
            int width = (int)value["m_Width"].Number, height = (int)value["m_Height"].Number, format = (int)value["m_TextureFormat"].Number;
            int faces = checked((int)(value.Find("m_ImageCount")?.Number ?? 1));
            if (faces < 1 || pixels.Bytes.Length % faces != 0) throw new InvalidDataException("纹理数据与图像数不符");
            byte[] rgba = new byte[checked(width * height * faces * 4)];
            int inputLength = pixels.Bytes.Length / faces, outputLength = rgba.Length / faces;
            for (int face = 0; face < faces; face++) Decode(pixels.Bytes.AsSpan(face * inputLength, inputLength), width, height, format, rgba.AsSpan(face * outputLength, outputLength));
            value = value.Set("image data", rgba).Set("m_TextureFormat", 4).Set("m_MipCount", 1).Set("m_CompleteImageSize", rgba.Length).Set("m_MipsStripped", 0);
        }
        return value;
    }

    internal static void Decode(ReadOnlySpan<byte> bytes, int width, int height, int format, Span<byte> rgba)
    {
        if (format is 28 or 29 or 64 or 65)
        {
            if (!AssetRipper.Conversions.UnityCrunch.UnityCrunch.TryDecompress(bytes, out byte[]? plain)) throw new InvalidDataException("Crunch 纹理解压失败");
            bytes = plain;
            format = format switch { 28 => 10, 29 => 12, 64 => 34, _ => 47 };
        }
        if (format is >= 48 and <= 53 or >= 66 and <= 71)
        {
            int block = new[] { 4, 5, 6, 8, 10, 12 }[format >= 66 ? format - 66 : format - 48];
            AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(bytes, width, height, block, block, rgba);
            return;
        }
        _ = format switch
        {
            1 => RgbConverter.Convert<ColorA<byte>, byte, ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            3 => RgbConverter.Convert<ColorRGB<byte>, byte, ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            4 => RgbConverter.Convert<ColorRGBA<byte>, byte, ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            7 => RgbConverter.Convert<ColorRGB16, byte, ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            10 => DxtDecoder.DecompressDXT1<ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            12 => DxtDecoder.DecompressDXT5<ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            13 => RgbConverter.Convert<ColorRGBA16, byte, ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            34 => EtcDecoder.DecompressETC<ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            41 => EtcDecoder.DecompressEACRUnsigned<ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            45 => EtcDecoder.DecompressETC2<ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            47 => EtcDecoder.DecompressETC2A8<ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            63 => RgbConverter.Convert<ColorR<byte>, byte, ColorRGBA<byte>, byte>(bytes, width, height, rgba),
            _ => throw new NotSupportedException($"纹理格式 {format}")
        };
    }

    public static void Write(AssetFile file, ObjectInfo item, Document document, Project project, Resources resources, Output output)
    {
        Value value = project.Read(file, item);
        byte[] data;
        string importer;
        Dictionary<string, object?> settings = new() { ["externalObjects"] = new Dictionary<string, object?>(), ["assetBundleName"] = document.Bundle, ["assetBundleVariant"] = "" };
        switch (document.ClassId)
        {
            case 49:
                var reader = file.Open(item);
                reader.Take(reader.Count()); reader.Align();
                data = reader.Take(reader.Count()).ToArray();
                importer = "TextScriptImporter";
                break;
            case 128:
                data = value["m_FontData"].Bytes;
                if (data.Length == 0) throw new NotSupportedException("字体没有原始字形数据");
                if (data.AsSpan().StartsWith("OTTO"u8)) document.Path = Path.ChangeExtension(document.Path, ".otf");
                importer = "TrueTypeFontImporter";
                settings["serializedVersion"] = 4;
                settings["fontSize"] = (int)value["m_FontSize"].Float;
                settings["includeFontData"] = 1;
                settings["ascentCalculationMode"] = 1;
                foreach (var field in new[] { ("forceTextureCase", "m_ConvertCase"), ("characterSpacing", "m_CharacterSpacing"), ("characterPadding", "m_CharacterPadding"),
                    ("fontRenderingMode", "m_FontRenderingMode"), ("useLegacyBoundsCalculation", "m_UseLegacyBoundsCalculation"), ("shouldRoundAdvanceValue", "m_ShouldRoundAdvanceValue") })
                    settings[field.Item1] = value[field.Item2].Data;
                settings["fontNames"] = value["m_FontNames"].Items.Select(v => (object)v.Text).ToArray();
                settings["fallbackFontReferences"] = value["m_FallbackFonts"].Items.Select(v => (object)new Yaml.Literal(project.Reference(file, document, v))).ToArray();
                break;
            case 83:
                if (!FsbLoader.TryLoadFsbFromByteArray(resources.Read(value["m_Resource"]), out var bank) || bank is null || bank.Samples.Count != 1)
                    throw new InvalidDataException("AudioClip 不是单样本 FSB5");
                if (!bank.Samples[0].RebuildAsStandardFileFormat(out var audio, out var extension) || audio is null || extension is null)
                    throw new NotSupportedException("FSB5 音频编码无法还原");
                data = audio;
                document.Path = Path.ChangeExtension(document.Path, "." + extension);
                importer = "AudioImporter";
                settings["serializedVersion"] = 7;
                settings["defaultSettings"] = new Dictionary<string, object?>
                {
                    ["serializedVersion"] = 2, ["loadType"] = value["m_LoadType"].Data, ["compressionFormat"] = value["m_CompressionFormat"].Data,
                    ["preloadAudioData"] = value["m_PreloadAudioData"].Data, ["sampleRateSetting"] = 0, ["sampleRateOverride"] = 44100, ["quality"] = 1, ["conversionMode"] = 0
                };
                settings["platformSettingOverrides"] = new Dictionary<string, object?>();
                settings["normalize"] = 1;
                settings["forceToMono"] = 0;
                settings["loadInBackground"] = value["m_LoadInBackground"].Data;
                settings["ambisonic"] = value["m_Ambisonic"].Data;
                settings["3D"] = value["m_Legacy3D"].Data;
                break;
            case 72:
                var kernels = value["variants"].Items.SelectMany(v => v["kernels"].Items).Select(k => k["name"].Text).Distinct().ToArray();
                output.Write(document.Path, "// ComputeShader 未恢复，仅保留内核名称。\n" + string.Join("\n", kernels.Select(k =>
                    $"#pragma kernel {k}\n[numthreads(1, 1, 1)] void {k}(uint3 id : SV_DispatchThreadID) {{ }}")) + "\n");
                output.Meta(document.Path, document.Guid, "ComputeShaderImporter", bundle: document.Bundle);
                output.Note("ComputeShader", document.Path, "内核方法体尚未恢复", true);
                return;
            case 48:
                string name = value.Find("m_ParsedForm")?.Find("m_Name")?.Text ?? value["m_Name"].Text;
                string source;
                try { source = Shader.Generate(value, reason => output.Note("Shader", document.Path, reason, !reason.StartsWith("关键字组合", StringComparison.Ordinal))); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException)
                {
                    source = $"// Shader 生成失败，详见 .resekai/report.json。\nShader {Yaml.Quote(name)} {{ SubShader {{ Pass {{ }} }} }}\n";
                    output.Note("Shader", document.Path, error.Message, true);
                }
                output.Write(document.Path, source);
                output.Meta(document.Path, document.Guid, "ShaderImporter", bundle: document.Bundle);
                return;
            default: throw new NotSupportedException($"导入类型 {document.ClassId}");
        }
        output.Write(document.Path, data);
        output.Write(document.Path + ".meta", $"fileFormatVersion: 2\nguid: {document.Guid}\n" + Yaml.Mapping(importer, settings));
    }
}
