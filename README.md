# 从木炉星把小侦察兵扔上太阳站

这是《星际拓荒》的程序辅助挑战复现模组。按 **F8**，模组从营火重开一个循环，回放录下的键鼠输入，乘船到雪山，微调站位和视角，并按预定物理步调用原生侦察兵发射器。侦察兵飞行、碰撞和附着由游戏处理。

## 安装和运行

记录环境为 **Windows、Steam《星际拓荒》1.1.16.1372、未启用 DLC、OWML 2.16.3**。先用 [Outer Wilds Mod Manager](https://github.com/ow-mods/ow-mod-man) 安装 OWML。

1. 从这个仓库的 Releases 下载 `SunStation.Control-0.6.4.zip`。
2. 解压到 OWML 的 `Mods` 目录。最终路径应为 `Mods/SunStation.Control/manifest.json`，同一目录还应有 DLL 和 `data/`。如果曾安装开发版，请用这个完整文件夹替换旧模组目录。
3. 通过 Mod Manager 启动，选择「继续探险」进入营火场景，按 **F8**。把游戏留在前台，发射前让模组接管操作。左上角会显示倒计时。
4. 发射一般在循环开始约 **4 分 49 秒**后，接近太阳站约在发射后 **3 分 52 秒**。发射后控制权交回玩家。为方便对照，可以等侦察兵离开木炉星一带再切星图。

发射前按 **F7** 或 **Esc** 可取消。发射后再按 **F8** 会开始一轮新回放。一次复跑约需 9 分钟。实测有命中和擦过，成功时 HUD 显示 `SCOUT ANCHORED`。附着对象以结果回执的 `anchorBody` 为准。

日志写在 Unity 的用户数据目录。Windows 下通常为

```text
%USERPROFILE%\AppData\LocalLow\Mobius Digital\Outer Wilds\SunStation.Control\runs
```

每次游戏启动生成一个会话目录。查找对应的 `route-shot-*-launch.json`、`route-shot-*.csv` 和 `route-shot-*-result.json`。

## 实测证据

视频所用的一发在本循环第 **17313** 个物理步发射，在发射后第 **13941** 个物理步（**232.35 秒**）附着太阳站。回执中 `anchorBody` 为 `SunStation_Body`，轨迹 CSV 的 `anchored` 从该步变为 `1`。

| 实测 | 结局 | 记录 |
| --- | --- | --- |
| 视频所用回放 | 确认附着太阳站 | [发射](evidence/video-hit/launch.json) · [轨迹](evidence/video-hit/trajectory.csv) · [结果](evidence/video-hit/result.json) |
| 同设置复跑 | 确认附着太阳站 | [发射](evidence/repeat-hit/launch.json) · [轨迹](evidence/repeat-hit/trajectory.csv) · [结果](evidence/repeat-hit/result.json) |
| 同设置复跑 | 掠过传送舱，最近采样间隙约 6.52 m | [发射](evidence/near-miss/launch.json) · [轨迹](evidence/near-miss/trajectory.csv) · [结果](evidence/near-miss/result.json) |

这三发来自 0.6.3 实测构建。0.6.4 演示包沿用该版的路线、实际成功站位、瞄准方向、发射时机和记录逻辑，运行数据改从安装包读取。CSV 保留原始数值。JSON 的本机绝对路径已改成发布目录中的相对名称。

另用 **0.6.4 安装 ZIP** 从独立 OWML 目录测试。第一次 F8 在山顶收尾超时，回放自动结束。第二次 F8 完成上山，在距目标 **2.06 mm** 处于第 **17313** 步原生发射，并以约 **15.67 m** 的最小采样间隙掠过传送舱。这次安装验证的轨迹记录到飞行 **251.00 秒**，期间未附着。[验证摘要](evidence/release-check/verification.json) · [发射回执](evidence/release-check/launch.json) · [轨迹](evidence/release-check/trajectory.csv)

发射使用 yaw **14.537644°**、pitch **47.33874°**，全程 1×，清醒等待。上山输入来自一次手动行走和驾驶记录，源码中的收尾控制负责毫米级站位修正。量子卫星、视角切换和实际发射初态可能带来跨循环差异。

## 从源码构建

需要 PowerShell、.NET SDK 或 Visual Studio Build Tools 提供的 Roslyn，以及本机安装的游戏和 OWML。指定 `OuterWilds.exe` 所在的目录和包含 `OWML.ModHelper.dll` 的 OWML 目录。

```powershell
.\Build.ps1 -GameDir 'C:\Games\Outer Wilds' -OwmlDir 'C:\Tools\OWML'
```

默认在 `dist/` 生成安装 ZIP 和 SHA-256 校验文件。编译时引用本机安装中的游戏和 OWML 程序集。安装包包含模组 DLL、manifest、输入记录和站位数据。
