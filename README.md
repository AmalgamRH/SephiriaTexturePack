# Sephiria 贴图替换 Mod (v1.0)

基于 BepInEx 5 的 Sephiria 贴图替换框架。替换游戏内任意贴图，无需修改游戏文件。

## 安装

1. 将解压后的文件夹里的内容**全部粘贴到游戏根目录**（`...\Steam\steamapps\common\Sephiria\`）
2. 启动游戏（双击 `Sephiria.exe` 或从 Steam 启动）
3. 首次运行自动生成配置，日志在 `BepInEx\LogOutput.log`

## 使用

### 基础使用：简单替换贴图（最常使用）

直接把您的替换图命名为**游戏内贴图名**，放入例如：

```
BepInEx\plugins\textures\logo.png
BepInEx\plugins\textures\N_Title.png
```

重启游戏即生效贴图更换。

**注意：替换图必须与原图尺寸一致**，否则将会被跳过并在日志中警告。

### 高级使用：使用配置文件自定义贴图文件名

如果您想自己决定替换的贴图名称或路径，可以在 `BepInEx\plugins\manifest.json` 中指定名称映射（相对插件目录的路径），例如：

```json
{
  "textures": {
    "logo": "textures/logo.png",
    "N_Title": "textures/custom/my_title.png"
  }
}
```

有 manifest 配置时按以下顺序解析：
1. manifest 有条目且文件存在 → 使用 manifest 映射
2. manifest 条目不合法（文件缺失）→ 回退到使用同名检测
3. manifest 无条目 → 使用同名检测（`textures/{贴图名}.png`）
4. 都没有 → 忽略该贴图

### 额外工具：如何查找原贴图名与尺寸？

**方式一（推荐，交互式）：** 直接双击 `tools\make_pack.exe`，按菜单提示选择功能并输入路径即可。

**方式二（命令行）：**

```bat
tools\make_pack.exe --list --lib <提取库目录> --out texture_list.csv
```

生成全部贴图的名称/尺寸/来源清单（CSV）。

### 打包工作流

**方式一（推荐，交互式）：** 双击 `tools\make_pack.exe`，选择"打包替换图"，按提示输入目录。

**方式二（命令行）：**

```bat
# 1. 把替换图放到一个目录（文件名 = 游戏贴图名）
# 2. 打包并校验尺寸
tools\make_pack.exe --in <替换图目录> --lib <提取库目录> --out <包目录>
# 3. 把包目录里的 manifest.json 和 textures/ 复制到 BepInEx\plugins\
```

## 卸载

删除游戏目录下的这三个东西，并且**使用Steam验证游戏文件完整性**即可完全还原：

```
winhttp.dll
doorstop_config.ini
BepInEx\
```

## 日志

`BepInEx\LogOutput.log` — 查看替换是否命中、尺寸警告、解码错误等。

## 免责声明

1. 本 Mod 为第三方爱好者制作，与游戏开发商及发行商无关，非官方产品。
2. 本 Mod 仅修改游戏运行时的显示内容，不修改游戏本体文件；但使用第三方 Mod 仍可能存在未知风险，请自行备份存档后使用，使用本 Mod 造成的一切后果由使用者自行承担。
3. 本 Mod 免费提供，仅供个人学习与娱乐使用，禁止用于任何商业用途。
4. 游戏更新可能导致 Mod 失效，届时请关注更新或暂时禁用本 Mod。

