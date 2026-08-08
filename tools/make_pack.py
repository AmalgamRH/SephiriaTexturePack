#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Sephiria 贴图替换 Mod 打包工具

用法:
  交互模式（双击运行 / 不带参数）:
    直接运行，按菜单提示选择功能并输入路径

  命令行模式（高级）:
    1) 生成贴图清单 CSV（找贴图名用）:
       python make_pack.py --list --lib <提取库> --out texture_list.csv

    2) 打包替换图:
       python make_pack.py --in <替换图目录> --lib <提取库> --out <包输出目录>

  工作流:
    - 把想替换的图做成 PNG，文件名 = 游戏内贴图名（如 logo.png、N_Title.png）
    - 运行打包命令，工具会对照提取库校验尺寸
    - 把输出的 manifest.json + textures/ 复制到游戏 BepInEx/plugins/
"""
import argparse
import csv
import json
import os
import shutil
import sys

# Windows 控制台统一 UTF-8（避免中文乱码；chcp 仅影响当前进程）
if sys.platform == "win32":
    os.system("chcp 65001 >nul")
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    try:
        sys.stdin.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

try:
    from PIL import Image
except ImportError:
    print("需要 Pillow: pip install pillow")
    sys.exit(1)

DEFAULT_LIB = r"D:\Steam\steamapps\common\Sephiria_extracted\Textures"


def build_index(lib):
    """提取库索引: {贴图名 -> (路径, 宽, 高)}"""
    idx = {}
    if not os.path.isdir(lib):
        print(f"错误: 提取库不存在: {lib}")
        sys.exit(1)
    for root, _, files in os.walk(lib):
        for f in files:
            if not f.lower().endswith(".png"):
                continue
            name = os.path.splitext(f)[0]
            p = os.path.join(root, f)
            try:
                im = Image.open(p)
                idx.setdefault(name, (p, im.width, im.height))
            except Exception:
                pass
    return idx


def cmd_list(lib, out):
    idx = build_index(lib)
    with open(out, "w", newline="", encoding="utf-8") as fp:
        w = csv.writer(fp)
        w.writerow(["name", "width", "height", "source"])
        for name, (p, w_, h_) in sorted(idx.items()):
            w.writerow([name, w_, h_, p])
    print(f"已生成 {len(idx)} 条清单 -> {out}")


def cmd_pack(src, lib, out):
    idx = build_index(lib)
    if not os.path.isdir(src):
        print(f"错误: 替换图目录不存在: {src}")
        sys.exit(1)
    out_textures = os.path.join(out, "textures")
    os.makedirs(out_textures, exist_ok=True)
    mapping = {}
    ok = warn = 0
    for f in sorted(os.listdir(src)):
        if not f.lower().endswith(".png"):
            continue
        name = os.path.splitext(f)[0]
        src_p = os.path.join(src, f)
        try:
            im = Image.open(src_p)
            sw, sh = im.width, im.height
        except Exception as e:
            print(f"  [跳过] {f}: 无法读取 ({e})")
            continue
        if name not in idx:
            print(f"  [警告] {f}: 提取库中无同名贴图，无法校验尺寸")
            warn += 1
        else:
            _, ow, oh = idx[name]
            if (sw, sh) != (ow, oh):
                print(f"  [警告] {f}: 尺寸 {sw}x{sh} != 原图 {ow}x{oh}（游戏内可能错位！）")
                warn += 1
        shutil.copy(src_p, os.path.join(out_textures, f))
        mapping[name] = "textures/" + f
        ok += 1
    if not mapping:
        print("没有可打包的图片")
        sys.exit(1)
    with open(os.path.join(out, "manifest.json"), "w", encoding="utf-8") as fp:
        json.dump({"textures": mapping}, fp, ensure_ascii=False, indent=2)
    print(f"完成: {ok} 张贴图 -> {out}")
    if warn:
        print(f"警告: {warn} 条（尺寸不一致或未校验）")
    print("安装: 将 manifest.json 和 textures/ 复制到游戏 BepInEx/plugins/")


# ---------------- 交互模式 ----------------

def ask(text, default=None):
    """带默认值的路径输入；支持拖拽文件（自动去引号）"""
    if default:
        prompt = f"{text}（回车用默认: {default}）: "
    else:
        prompt = f"{text}: "
    v = input(prompt).strip().strip('"').strip()
    if not v and default:
        v = default
    return v


def interactive():
    print()
    print("=" * 52)
    print("  Sephiria 贴图替换 Mod - 打包工具 v1.0")
    print("=" * 52)
    while True:
        print()
        print("请选择功能:")
        print("  1. 生成贴图清单 CSV（查找贴图名/尺寸）")
        print("  2. 打包替换图（校验尺寸 + 生成 manifest）")
        print("  3. 退出")
        choice = input("请输入数字 [1/2/3]: ").strip()
        if choice == "1":
            lib = ask("提取库目录", DEFAULT_LIB)
            out = ask("输出 CSV 路径", "texture_list.csv")
            try:
                cmd_list(lib, out)
            except SystemExit:
                pass
            except Exception as e:
                print(f"出错: {e}")
        elif choice == "2":
            src = ask("替换图目录（PNG 文件名 = 游戏贴图名）")
            if not src:
                print("未输入目录，返回菜单")
                continue
            lib = ask("提取库目录", DEFAULT_LIB)
            out = ask("输出包目录", "pack")
            try:
                cmd_pack(src, lib, out)
            except SystemExit:
                pass
            except Exception as e:
                print(f"出错: {e}")
        elif choice in ("3", "q", "Q", "exit"):
            print("再见！")
            break
        else:
            print("无效输入，请重新选择")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="Sephiria 贴图替换 mod 打包工具")
    ap.add_argument("--lib", default=None, help="提取库目录")
    ap.add_argument("--list", action="store_true", help="生成贴图清单 CSV")
    ap.add_argument("--out", help="输出路径（CSV 路径或包目录）")
    ap.add_argument("--in", dest="src", help="替换图目录")
    args = ap.parse_args()

    has_cmd = args.list or args.src or args.out or args.lib
    if not has_cmd:
        # 无参数 → 交互模式
        interactive()
    else:
        # 命令行模式（保持向后兼容）
        if args.list:
            if not args.out:
                ap.error("--list 需要 --out")
            cmd_list(args.lib or DEFAULT_LIB, args.out)
        else:
            if not args.src or not args.out:
                ap.error("打包需要 --in 和 --out")
            cmd_pack(args.src, args.lib or DEFAULT_LIB, args.out)
