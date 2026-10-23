# HitTextTweaker

《A Dance of Fire and Ice》判定飘字自定义 mod。

把游戏里的「完美！」「稍快！」「太慢！」等判定文字改成你喜欢的字样（比如「？！强强！？」），还支持自定义颜色和渐变。

## 功能

- 11 个判定档位逐个自定义文字（留空 = 保持游戏原样）
- 改完立即生效，不用重启游戏
- 关卡编辑器里试玩同样实时生效，不用重开谱子
- 每档独立颜色：RGBA 滑条调色，默认显示游戏原色
- 渐变：横向（左→右）、纵向（上→下）、四角（左上/右上/左下/右下）
- 「整体渐变」：整段文字从左到右渐变，或退回每个字内部渐变
- UMM 游戏内设置面板，保存到 `config.json`
- UMM 里关掉 mod 开关，判定文字立即恢复游戏原样

## 安装

1. 游戏需装好 UnityModManager（游戏自带 UMM 0.32.5.0 即可，无需额外安装）
2. 把 `HitTextTweaker` 文件夹放进游戏的 `Mods\` 文件夹
   （最终结构：`Mods\HitTextTweaker\Info.json` 和 `Mods\HitTextTweaker\HitTextTweaker.dll`）
3. 启动游戏，打开 UMM 的 Mod Manager（默认 `Ctrl+F10`，若无效请查看 UMM 自己的热键设置）
   列表里出现 `HitTextTweaker`，状态 `Active` 即安装成功

## 使用

- 在 UMM 列表点本 mod 右侧的齿轮/设置按钮，打开设置面板
- 所有档位默认留空：不填 = 保持游戏原样，本 mod 不带任何预设文字
- 每个判定档位一个输入框，输入想显示的文字（推荐格式「？！X！？」）
- 改完立即生效：下一次判定就用新字，不用重启游戏
- 点「保存」写入 `config.json`，重启后仍保留
- 输入框留空 = 该档位保持游戏原样
- 在 UMM 里关掉本 mod 的开关后，判定文字会立即恢复游戏原样

### 颜色与渐变

- 每个档位点「调色」展开 R/G/B/A 四条滑条（0.00~1.00），旁边色块实时预览
- 「恢复游戏原色」一键还原该档位的游戏默认颜色
- 开「渐变色」后可选方向：
  - 横向（左→右）：字内部从左到右渐变，2 个颜色
  - 纵向（上→下）：字内部从上到下渐变，2 个颜色
  - 四角：左上/右上/左下/右下 4 个颜色，进阶玩法
- 「整体渐变」按钮（单字渐变 / 整段文字渐变）：
  - 关 = 每个字内部渐变（横向/纵向/四角）
  - 开 + 横向 = 整段文字从左到右渐变
  - 开 + 纵向/四角 = 自动退回每个字内部渐变（单行文字没法按行上下铺）

## 从源码构建

需要：

- Windows PowerShell 5.1（不要用 pwsh 7，`Add-Type` 会报 CS1069）
- 游戏安装目录（用于引用游戏自带的 Unity 程序集）

```powershell
.\build.ps1 -GameDir "D:\steam\steamapps\common\A Dance of Fire and Ice"
```

脚本会读取 `src\Main.cs`，引用游戏 `_Data\Managed` 里的程序集（UnityEngine、TextMeshPro、UnityModManager、0Harmony 等），编译到 `out\HitTextTweaker.dll`。

也可以设置环境变量 `ADOFAI_DIR` 指向游戏目录，然后直接运行 `.\build.ps1`。

## 项目结构

```
build.ps1        编译脚本（PowerShell 5.1）
src/Main.cs      mod 源码（Harmony patch + UMM 设置界面）
dist/            发行包 zip
```

## 更新日志

### v1.5.5（2026-10-23）

- 整体渐变遇到纵向/四角方向时，自动改为每个字内部渐变（单行文字无法整段上下渐变）

### v1.5.x

- 整体渐变改用富文本实现，修复判定文字淡出后留白印/残影
- 删除彩虹预设，改为「整体渐变」开关（单字内部渐变 / 整个文本渐变）
- 渐变颜色由 hex 输入改为 RGBA 滑条
- 新增渐变方向：横向 / 纵向 / 四角

### v1.2.x

- v1.2.3：删除内置预设，安装后所有档位默认留空，完全由使用者自己填
- v1.2.2：修复关卡编辑器里换字后判定文字不刷新
- v1.2.1：修复关掉 mod 开关后判定文字仍显示自定义内容
- v1.2.0：加入游戏内设置面板，11 个档位逐个自定义

## 许可

MIT License，详见 [LICENSE](LICENSE)。

## 致谢

- [7th Beat Games](https://7thbe.at/) 的《A Dance of Fire and Ice》
- [UnityModManager](https://github.com/newman55/unity-mod-manager)
- [Harmony](https://github.com/pardeike/Harmony)
