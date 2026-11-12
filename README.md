# ReSekai

导出为 Unity 2022.3.62f2 工程。

- `dotnet run --project src/ReSekai -c Release -- game.apk project` 导出 Unity 工程，省略输出目录时生成同名 `.unity` 目录。
- `dotnet run --project src/ReSekai -c Release -- extract game.apk il2cpp` 提取二进制和解密后的 metadata，省略输出目录时生成同名 `.il2cpp` 目录。
- `--resources <目录>` 加入同版本服务器资源。
- `--scripts stubs|full` 导出空桩或恢复方法体，默认 `stubs`。
- `--upm auto|none|包名列表` 自动选择 UPM 包、禁用 UPM 或指定包名（逗号分隔），默认 `auto`。

## 引用项目

- 脚本和 metadata 使用 [Cpp2CS](https://github.com/MetaMikuAI/Cpp2CS)、[Il2CppDumper](https://github.com/Perfare/Il2CppDumper)。
- 纹理和音频使用 [TextureDecoder](https://github.com/AssetRipper/TextureDecoder)、[UnityCrunch](https://github.com/AssetRipper/AssetRipper.Conversions.Crunch)、[Fmod5Sharp](https://github.com/SamboyCoding/Fmod5Sharp)。
