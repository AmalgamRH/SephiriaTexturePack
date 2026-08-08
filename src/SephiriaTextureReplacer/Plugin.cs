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
    [BepInPlugin("com.sisyphus.sephiriatexturereplacer", "Sephiria Texture Replacer", "1.0.0")]
    public class SephiriaTextureReplacerPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private static string PluginDir;
        private static string TextureDir;
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>();
        private static bool UseManifest;
        private static readonly Dictionary<string, Sprite> SpriteCache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Texture2D> TextureCache = new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> Missing = new HashSet<string>();
        private static readonly HashSet<UnityEngine.Object> ReplacedInstances = new HashSet<UnityEngine.Object>();
        private static Harmony _harmony;
        private float _scanTimer;

        private void Awake()
        {
            Log = Logger;
            PluginDir = Path.GetDirectoryName(typeof(SephiriaTextureReplacerPlugin).Assembly.Location);
            TextureDir = Path.Combine(PluginDir, "textures");
            if (!Directory.Exists(TextureDir))
                Directory.CreateDirectory(TextureDir);
            LoadManifest();

            _harmony = new Harmony("com.sisyphus.sephiriatexturereplacer");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Image), "set_sprite"),
                    prefix: new HarmonyMethod(typeof(SephiriaTextureReplacerPlugin).GetMethod(nameof(ImageSetSpritePrefix), BindingFlags.NonPublic | BindingFlags.Static)));
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
            Log.LogInfo("[STR] v0.3.0 engine ready (" + (UseManifest ? "manifest mode" : "name-convention mode") + ")");
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

        private static void ImageSetSpritePrefix(ref Sprite value)
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
                string key = tex != null ? tex.name : s.name;
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

        // ---------- PNG 解码（自研 + GDI+ 兜底） ----------

        private static Texture2D LoadTexture(string path, string name, bool mipChain, FilterMode filter, TextureWrapMode wrap)
        {
            var bytes = File.ReadAllBytes(path);
            Color32[] pixels;
            int w, h;
            try
            {
                pixels = DecodePng(bytes, out w, out h);
            }
            catch (Exception e)
            {
                Log.LogWarning("[STR] internal PNG decode failed (" + e.Message + "), fallback to GDI+");
                pixels = DecodePngGdiPlus(path, out w, out h);
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain);
            tex.name = name;
            // 复制原纹理采样参数（像素游戏多为 Point 最近邻，否则放大模糊）
            tex.filterMode = filter;
            tex.wrapMode = wrap;
            tex.SetPixels32(pixels);
            tex.Apply(mipChain, false);
            return tex;
        }

        private static Color32[] DecodePng(byte[] data, out int w, out int h)
        {
            if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50)
                throw new Exception("not a png");
            w = 0; h = 0;
            int pos = 8;
            int bitDepth = 8, colorType = 0;
            byte[] idat = null;
            var palette = new Color32[256];
            var trns = new byte[256];
            bool hasTrns = false;
            while (pos + 8 <= data.Length)
            {
                int len = ReadBE32(data, pos); pos += 4;
                if (len < 0 || pos + 4 + len + 4 > data.Length) break;
                string type = System.Text.Encoding.ASCII.GetString(data, pos, 4); pos += 4;
                int chunkStart = pos;
                pos += len + 4; // data + crc
                if (type == "IHDR" && len >= 13)
                {
                    w = ReadBE32(data, chunkStart); h = ReadBE32(data, chunkStart + 4);
                    bitDepth = data[chunkStart + 8]; colorType = data[chunkStart + 9];
                }
                else if (type == "IDAT")
                {
                    if (idat == null)
                    {
                        idat = new byte[len];
                        Array.Copy(data, chunkStart, idat, 0, len);
                    }
                    else
                    {
                        var n = new byte[idat.Length + len];
                        Array.Copy(idat, 0, n, 0, idat.Length);
                        Array.Copy(data, chunkStart, n, idat.Length, len);
                        idat = n;
                    }
                }
                else if (type == "PLTE")
                {
                    for (int i = 0; i + 2 < len; i += 3)
                        palette[i / 3] = new Color32(data[chunkStart + i], data[chunkStart + i + 1], data[chunkStart + i + 2], 255);
                }
                else if (type == "tRNS")
                {
                    hasTrns = true;
                    Array.Copy(data, chunkStart, trns, 0, Math.Min(len, trns.Length));
                }
            }
            if (bitDepth != 8)
                throw new Exception("unsupported bit depth " + bitDepth);
            if (colorType != 0 && colorType != 2 && colorType != 3 && colorType != 4 && colorType != 6)
                throw new Exception("unsupported color type " + colorType);
            int bpp = colorType == 0 ? 1 : colorType == 2 ? 3 : colorType == 3 ? 1 : colorType == 4 ? 2 : 4;
            int stride = w * bpp; // bitDepth=8 时每像素字节数 = bpp
            var raw = Inflate(idat, (stride + 1) * h);
            var rows = new byte[h][];
            for (int y = 0; y < h; y++)
            {
                int off = y * (stride + 1);
                byte filter = raw[off];
                var row = new byte[stride];
                Array.Copy(raw, off + 1, row, 0, stride);
                Unfilter(row, filter, y > 0 ? rows[y - 1] : null, bpp);
                rows[y] = row;
            }
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int destY = h - 1 - y; // Unity y=0 在底部
                var row = rows[y];
                for (int x = 0; x < w; x++)
                {
                    byte r, g, b, a;
                    switch (colorType)
                    {
                        case 0:
                            r = g = b = row[x];
                            a = (hasTrns && row[x] == trns[0]) ? (byte)0 : (byte)255;
                            break;
                        case 2:
                            r = row[x * 3]; g = row[x * 3 + 1]; b = row[x * 3 + 2]; a = 255;
                            break;
                        case 3:
                        {
                            var p = palette[row[x]];
                            r = p.r; g = p.g; b = p.b;
                            a = (hasTrns && row[x] < trns.Length) ? trns[row[x]] : p.a;
                            break;
                        }
                        case 4:
                            r = g = b = row[x * 2]; a = row[x * 2 + 1];
                            break;
                        default:
                            r = row[x * 4]; g = row[x * 4 + 1]; b = row[x * 4 + 2]; a = row[x * 4 + 3];
                            break;
                    }
                    pixels[destY * w + x] = new Color32(r, g, b, a);
                }
            }
            return pixels;
        }

        private static byte[] Inflate(byte[] zlib, int expected)
        {
            var buf = new byte[expected];
            using (var ms = new MemoryStream(zlib, 2, zlib.Length - 2))
            using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
            {
                int read = 0;
                while (read < expected)
                {
                    int n = ds.Read(buf, read, expected - read);
                    if (n <= 0) break;
                    read += n;
                }
                Array.Resize(ref buf, read);
            }
            if (buf.Length < expected)
                throw new Exception("inflate short (" + buf.Length + "/" + expected + ")");
            return buf;
        }

        private static void Unfilter(byte[] row, byte filter, byte[] prev, int bpp)
        {
            switch (filter)
            {
                case 0: break;
                case 1:
                    for (int i = bpp; i < row.Length; i++) row[i] = (byte)(row[i] + row[i - bpp]);
                    break;
                case 2:
                    if (prev != null)
                        for (int i = 0; i < row.Length; i++) row[i] = (byte)(row[i] + prev[i]);
                    break;
                case 3:
                    for (int i = 0; i < row.Length; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        int b = prev != null ? prev[i] : 0;
                        row[i] = (byte)(row[i] + ((a + b) / 2));
                    }
                    break;
                case 4:
                    for (int i = 0; i < row.Length; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        int b = prev != null ? prev[i] : 0;
                        int c = (i >= bpp && prev != null) ? prev[i - bpp] : 0;
                        int p = a + b - c;
                        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        int pr = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                        row[i] = (byte)(row[i] + pr);
                    }
                    break;
                default:
                    throw new Exception("bad filter " + filter);
            }
        }

        private static int ReadBE32(byte[] d, int o)
        {
            return (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
        }

        private static Color32[] DecodePngGdiPlus(string path, out int w, out int h)
        {
            using (var bmp = new System.Drawing.Bitmap(path))
            {
                w = bmp.Width; h = bmp.Height;
                var px = new Color32[w * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var c = bmp.GetPixel(x, h - 1 - y);
                        px[y * w + x] = new Color32(c.R, c.G, c.B, c.A);
                    }
                }
                return px;
            }
        }
    }
}
