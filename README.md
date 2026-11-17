# ReSekai

导出为 Unity 2022.3.62f2 工程。

- `dotnet run --project src/ReSekai -c Release -- game.apk project` 导出 Unity 工程，省略输出目录时生成同名 `.unity` 目录。
- `dotnet run --project src/ReSekai -c Release -- extract game.apk il2cpp` 提取二进制和解密后的 metadata，省略输出目录时生成同名 `.il2cpp` 目录。
- `--resources <目录>` 加入同版本服务器资源。
- `--scripts stubs|full` 导出空桩或恢复方法体，默认 `stubs`。
- `--upm auto|none|包名列表` 自动选择 UPM 包、禁用 UPM 或指定包名（逗号分隔），默认 `auto`。
- 完整导出会随依赖流程接入 Cubism Unity SDK 与 Purism Core Windows x64 插件。手动选包时使用 `com.live2d.cubism`。
- 完整导出保留 APK 的第三方 Android ARM64 原生库，包括 Live2D、CRIWARE、Firebase。游戏 IL2CPP、Unity 引擎和 Burst 生成库由 Unity 重建。Android 构建会接入 Firebase 和 Play Games 的 Java 依赖，并使用 APK 内的 Firebase 配置。Google Maven 无法访问时可用 `RESEKAI_GOOGLE_MAVEN` 指向 HTTPS 镜像。
- 含 CRIWARE 的完整导出会保留 APK 的 Android ARM64 原生库。将 `RESEKAI_CRIWARE_SDK` 设为 `criware_sdk_unity_v3_14_00_smartphone_en.zip` 的路径，或把 ZIP 放入 `RESEKAI_PACKAGE_CACHE`，即可同时导出 Windows x64 的配套封装和插件。
- `--recovered-scripts <目录>` 在完整模式复用同一 APK 已完成的 `.resekai/scripts` 中的恢复程序集，按当前规则重新生成源码并导出到空目录。

## 引用项目

- 脚本和 metadata 使用 [Cpp2CS](https://github.com/MetaMikuAI/Cpp2CS)、[Il2CppDumper](https://github.com/Perfare/Il2CppDumper)。
- 游戏引用的 `System.Runtime.CompilerServices.Unsafe` 由同名 NuGet 包提供，导出为托管插件并附带许可证。
- 纹理和音频使用 [TextureDecoder](https://github.com/AssetRipper/TextureDecoder)、[UnityCrunch](https://github.com/AssetRipper/AssetRipper.Conversions.Crunch)、[Fmod5Sharp](https://github.com/SamboyCoding/Fmod5Sharp)。
