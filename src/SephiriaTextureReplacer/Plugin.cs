using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;

namespace SephiriaTextureReplacer
{
    [BepInPlugin("com.sisyphus.sephiriatexturereplacer", "Sephiria Texture Replacer", Version)]
    public class SephiriaTextureReplacerPlugin : BaseUnityPlugin
    {
        private const string Version = "1.0.1";

        internal static ManualLogSource Log;
        private static string PluginDir;
        private static string TextureDir;
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>();
        private static bool UseManifest;
        private static readonly Dictionary<string, Sprite> SpriteCache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Texture2D> TextureCache = new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> Missing = new HashSet<string>();
        private static readonly HashSet<UnityEngine.Object> ReplacedInstances = new HashSet<UnityEngine.Object>();
        private static Func<Texture2D, byte[], bool> _loadImage;
        private static Harmony _harmony;
        private float _scanTimer;

        private void Awake()
        {
            Log = Logger;
            _loadImage = BindLoadImage();
            PluginDir = Path.GetDirectoryName(typeof(SephiriaTextureReplacerPlugin).Assembly.Location);
            TextureDir = Path.Combine(PluginDir, "textures");
            if (!Directory.Exists(TextureDir))
                Directory.CreateDirectory(TextureDir);
            LoadManifest();

            _harmony = new Harmony("com.sisyphus.sephiriatexturereplacer");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(SpriteRenderer), "set_sprite"),
                    prefix: new HarmonyMethod(typeof(SephiriaTextureReplacerPlugin).GetMethod(nameof(SpriteSetPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                Log.LogInfo("[STR] SpriteRenderer.set_sprite patched OK");
            }
            catch (Exception e)
            {
                Log.LogWarning("[STR] SpriteRenderer.set_sprite patch FAILED: " + e.Message);
            }
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Image), "set_sprite"),
                    prefix: new HarmonyMethod(typeof(SephiriaTextureReplacerPlugin).GetMethod(nameof(SpriteSetPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                Log.LogInfo("[STR] Image.set_sprite patched OK");
            }
            catch (Exception e)
            {
                Log.LogWarning("[STR] Image.set_sprite patch FAILED: " + e.Message);
            }
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(RawImage), "set_texture"),
                    prefix: new HarmonyMethod(typeof(SephiriaTextureReplacerPlugin).GetMethod(nameof(RawImageSetTexturePrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                Log.LogInfo("[STR] RawImage.set_texture patched OK");
            }
            catch (Exception e)
            {
                Log.LogWarning("[STR] RawImage.set_texture patch FAILED: " + e.Message);
            }

            ScanAll();
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => ScanAll();
            Log.LogInfo("[STR] v" + Version + " engine ready (" + (UseManifest ? "manifest mode" : "name-convention mode") + ")");
        }

        private void Update()
        {
            _scanTimer -= Time.deltaTime;
            if (_scanTimer <= 0f)
            {
                _scanTimer = 0.5f;
                ScanAll();
            }
        }

        // ---------- manifest ----------

        private void LoadManifest()
        {
            string manifestPath = Path.Combine(PluginDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                Log.LogInfo("[STR] no manifest.json, using name-convention mode");
                return;
            }
            try
            {
                var doc = JsonConvert.DeserializeObject<ManifestDoc>(File.ReadAllText(manifestPath));
                if (doc != null && doc.textures != null)
                {
                    foreach (var kv in doc.textures)
                        Map[kv.Key] = kv.Value;
                    UseManifest = true;
                    Log.LogInfo("[STR] manifest loaded: " + Map.Count + " entries");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[STR] manifest load FAILED (" + e.Message + "), using name-convention mode");
            }
        }

        private class ManifestDoc
        {
            public Dictionary<string, string> textures { get; set; }
        }

        // ---------- 路径解析 ----------

        private static string ResolveTexturePath(string key)
        {
            // 1) manifest 显式映射优先（条目文件必须存在，否则回退）
            if (UseManifest && Map.TryGetValue(key, out var rel))
            {
                var mp = Path.Combine(PluginDir, rel);
                if (File.Exists(mp)) return mp;
                Log.LogWarning($"[STR] manifest entry '{key}' -> '{rel}' not found, fallback to name-convention");
            }
            // 2) 同名约定兜底（混合模式：放图即用）
            string p = Path.Combine(TextureDir, key + ".png");
            return File.Exists(p) ? p : null;
        }

        // ---------- 扫描（通道 B 兜底） ----------

        private static void ScanAll()
        {
            try
            {
                // 清理已销毁对象的引用：避免 HashSet 无限增长（内存泄漏），
                // 同时防止 Unity instanceID 被回收复用后把新对象误判为“已替换”而跳过。
                // （UnityEngine.Object 重载了 ==，已销毁对象与 null 比较为 true）
                ReplacedInstances.RemoveWhere(o => o == null);

                foreach (var sr in UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None))
                {
                    if (ReplacedInstances.Contains(sr)) continue;
                    var s = sr.sprite;
                    if (s == null) continue;
                    var repl = GetReplacement(s);
                    if (repl == null) continue;
                    sr.sprite = repl;
                    ReplacedInstances.Add(sr);
                }
                foreach (var img in UnityEngine.Object.FindObjectsByType<Image>(FindObjectsSortMode.None))
                {
                    if (ReplacedInstances.Contains(img)) continue;
                    var s = img.sprite;
                    if (s == null) continue;
                    var repl = GetReplacement(s);
                    if (repl == null) continue;
                    img.sprite = repl;
                    ReplacedInstances.Add(img);
                }
                foreach (var raw in UnityEngine.Object.FindObjectsByType<RawImage>(FindObjectsSortMode.None))
                {
                    if (ReplacedInstances.Contains(raw)) continue;
                    var t = raw.texture as Texture2D;
                    if (t == null) continue;
                    var repl = GetReplacementTexture(t);
                    if (repl == null) continue;
                    raw.texture = repl;
                    ReplacedInstances.Add(raw);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[STR] ScanAll error: " + e.Message);
            }
        }

        // ---------- patch 通道 A ----------

        /// <summary>
        /// SpriteRenderer 与 Image 共用的 sprite setter 前缀。
        /// 游戏内的 2D 动画（Animator2D_SpriteRenderer / SimpleAnimator2D）是脚本驱动，
        /// 每帧执行 spriteRenderer.sprite = 当前帧，因此必须在 setter 上拦截；
        /// 仅靠 ScanAll 轮询 + 实例标记只能命中某一帧，无法替换动图。
        /// </summary>
        private static void SpriteSetPrefix(ref Sprite value)
        {
            if (value == null) return;
            var repl = GetReplacement(value);
            if (repl != null) value = repl;
        }

        private static void RawImageSetTexturePrefix(ref Texture value)
        {
            var t = value as Texture2D;
            if (t == null) return;
            var repl = GetReplacementTexture(t);
            if (repl != null) value = repl;
        }

        // ---------- 替换核心 ----------

        private static Sprite GetReplacement(Sprite s)
        {
            try
            {
                if (s == null) return null;
                var tex = s.texture;
                // tex.name 可能为空（图集/运行时合成纹理），此时回退到 Sprite 名称
                string key = (tex != null && !string.IsNullOrEmpty(tex.name)) ? tex.name : s.name;
                if (string.IsNullOrEmpty(key)) return null;
                if (Missing.Contains(key)) return null;
                if (SpriteCache.TryGetValue(key, out var cached)) return cached;

                string pngPath = ResolveTexturePath(key);
                if (pngPath == null || !File.Exists(pngPath))
                {
                    Missing.Add(key);
                    return null;
                }

                var newTex = LoadTexture(pngPath, key, tex.mipmapCount > 1, tex.filterMode, tex.wrapMode);
                if (newTex == null)
                {
                    Missing.Add(key);
                    return null;
                }
                if (newTex.width != tex.width || newTex.height != tex.height)
                {
                    Log.LogWarning($"[STR] SIZE MISMATCH '{key}': replaced={newTex.width}x{newTex.height} original={tex.width}x{tex.height} -> skipped (keep original size)");
                    Missing.Add(key);
                    UnityEngine.Object.Destroy(newTex);
                    return null;
                }

                // Sprite.pivot getter 返回像素坐标；Sprite.Create 需要归一化（0~1）
                var pivotNorm = new Vector2(
                    s.rect.width > 0f ? s.pivot.x / s.rect.width : 0.5f,
                    s.rect.height > 0f ? s.pivot.y / s.rect.height : 0.5f);
                var ns = Sprite.Create(
                    newTex,
                    s.rect,
                    pivotNorm,
                    s.pixelsPerUnit,
                    0,
                    SpriteMeshType.FullRect,
                    s.border);
                ns.name = s.name + "_repl";
                SpriteCache[key] = ns;
                Log.LogInfo($"[STR] replaced sprite '{s.name}' <- tex '{key}' ({newTex.width}x{newTex.height})");
                return ns;
            }
            catch (Exception e)
            {
                Log.LogWarning("[STR] GetReplacement error for '" + s?.name + "': " + e.Message);
                if (s != null && s.texture != null) Missing.Add(s.texture.name);
                return null;
            }
        }

        private static Texture2D GetReplacementTexture(Texture2D t)
        {
            try
            {
                if (t == null) return null;
                string key = t.name;
                if (string.IsNullOrEmpty(key)) return null;
                if (Missing.Contains(key)) return null;
                if (TextureCache.TryGetValue(key, out var cached)) return cached;

                string pngPath = ResolveTexturePath(key);
                if (pngPath == null || !File.Exists(pngPath))
                {
                    Missing.Add(key);
                    return null;
                }

                var newTex = LoadTexture(pngPath, key, t.mipmapCount > 1, t.filterMode, t.wrapMode);
                if (newTex == null)
                {
                    Missing.Add(key);
                    return null;
                }
                if (newTex.width != t.width || newTex.height != t.height)
                {
                    Log.LogWarning($"[STR] SIZE MISMATCH '{key}': replaced={newTex.width}x{newTex.height} original={t.width}x{t.height} -> skipped (keep original size)");
                    Missing.Add(key);
                    UnityEngine.Object.Destroy(newTex);
                    return null;
                }
                TextureCache[key] = newTex;
                Log.LogInfo($"[STR] replaced raw texture '{key}' ({newTex.width}x{newTex.height})");
                return newTex;
            }
            catch (Exception e)
            {
                Log.LogWarning("[STR] GetReplacementTexture error for '" + t?.name + "': " + e.Message);
                if (t != null) Missing.Add(t.name);
                return null;
            }
        }

        // ---------- PNG 解码（Unity 内置 ImageConversionModule） ----------

        /// <summary>
        /// 绑定 ImageConversion.LoadImage(Texture2D, byte[]) 为委托。
        /// 原因：Unity 6 的 ImageConversion 另有 ReadOnlySpan&lt;byte&gt; 重载，而本项目目标 net472、
        /// 游戏程序集为 netstandard 2.1（Span 经 mscorlib 转发），编译期无法解析该类型（CS0518）。
        /// 用反射直接绑定无 Span 的 byte[] 重载，既保留引擎解码器，又无需新增依赖或改动目标框架。
        /// </summary>
        private static Func<Texture2D, byte[], bool> BindLoadImage()
        {
            try
            {
                var mi = typeof(ImageConversion).GetMethod(
                    "LoadImage",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Texture2D), typeof(byte[]) },
                    null);
                if (mi == null)
                {
                    Log.LogWarning("[STR] ImageConversion.LoadImage(Texture2D, byte[]) not found");
                    return null;
                }
                var fn = (Func<Texture2D, byte[], bool>)Delegate.CreateDelegate(typeof(Func<Texture2D, byte[], bool>), mi);
                Log.LogInfo("[STR] PNG decoder: engine ImageConversion.LoadImage bound");
                return fn;
            }
            catch (Exception e)
            {
                Log.LogWarning("[STR] ImageConversion.LoadImage bind FAILED: " + e.Message);
                return null;
            }
        }

        private static Texture2D LoadTexture(string path, string name, bool mipChain, FilterMode filter, TextureWrapMode wrap)
        {
            Texture2D tex = null;
            try
            {
                var bytes = File.ReadAllBytes(path);

                // 引擎解码器只认 PNG / JPEG，且靠内容而非扩展名判断。
                // 提前报出可读错误（常见坑：把 GIF / JPG 直接改名成 .png）。
                if (!IsPngOrJpeg(bytes))
                {
                    Log.LogWarning("[STR] '" + name + "' 不是 PNG/JPEG（文件头 " + HexHead(bytes) + "），已跳过；请另存为 PNG 后重试");
                    return null;
                }

                // 使用 Unity 引擎内置解码器（UnityEngine.ImageConversionModule）。
                // 覆盖此前自研解码器不支持的场景：16 位色深、Adam7 隔行扫描、
                // 调色板 tRNS 透明色、灰度 tRNS 等；无需额外引入第三方 PNG 库。
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain);
                if (_loadImage == null || !_loadImage(tex, bytes))
                {
                    Log.LogWarning("[STR] PNG decode failed for '" + name + "'");
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[STR] PNG decode failed for '" + name + "': " + e.Message);
                if (tex != null) UnityEngine.Object.Destroy(tex);
                return null;
            }

            tex.name = name;
            // 复制原纹理采样参数（像素游戏多为 Point 最近邻，否则放大模糊）
            tex.filterMode = filter;
            tex.wrapMode = wrap;
            return tex;
        }

        // PNG 签名 89 50 4E 47；JPEG 起始 FF D8 FF
        private static bool IsPngOrJpeg(byte[] b)
        {
            if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
            return false;
        }

        private static string HexHead(byte[] b)
        {
            int n = Math.Min(4, b.Length);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
