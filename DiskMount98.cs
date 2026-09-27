// DiskMount98 - PC-98 DOSフロッピーイメージ(NFD/D88/HDM)を仮想ドライブとしてエクスプローラーで開く
//
// イメージ内のFATを一時フォルダへ展開してドライブ文字を割り当て、
// フォルダの変更を検知したらFATを組み直してイメージへ書き戻す。ドライバ不要。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DiskMount98
{
    class RetryException : Exception
    {
        public RetryException(string msg) : base(msg) { }
    }

    // ---------------------------------------------------------------- イメージコンテナ

    class Sector
    {
        public int C, H, R, N, Offset, Size;
    }

    class DiskImage
    {
        public string FilePath;
        public string Format;
        public byte[] Raw;
        public bool WriteProtect;
        public List<Sector>[] Tracks = new List<Sector>[164];

        public static uint U32(byte[] b, int o) { return (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24); }
        public static int U16(byte[] b, int o) { return b[o] | b[o + 1] << 8; }

        public static DiskImage Load(string path)
        {
            DiskImage d = new DiskImage();
            d.FilePath = path;
            d.Raw = File.ReadAllBytes(path);
            for (int i = 0; i < d.Tracks.Length; i++) d.Tracks[i] = new List<Sector>();
            byte[] r = d.Raw;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string id = r.Length >= 0x120 ? Encoding.ASCII.GetString(r, 0, 14) : "";
            if (id == "T98FDDIMAGE.R0") d.LoadNfd0();
            else if (id == "T98FDDIMAGE.R1") d.LoadNfd1();
            else if (ext == ".d88" || ext == ".d98" || ext == ".88d" || (r.Length >= 0x2B0 && U32(r, 0x1C) == r.Length)) d.LoadD88();
            else d.LoadRaw();
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) d.WriteProtect = true;
            return d;
        }

        void Add(int track, int c, int h, int rr, int n, int offset, int size)
        {
            if (track < 0 || track >= Tracks.Length) return;
            if (offset < 0 || size <= 0 || offset + size > Raw.Length) return;
            Sector s = new Sector();
            s.C = c; s.H = h; s.R = rr; s.N = n; s.Offset = offset; s.Size = size;
            Tracks[track].Add(s);
        }

        void LoadNfd0()
        {
            Format = "NFD r0";
            WriteProtect = Raw[0x114] != 0;
            int ptr = (int)U32(Raw, 0x110);
            for (int i = 0; i < 163; i++)
            {
                for (int j = 0; j < 26; j++)
                {
                    int e = 0x120 + (i * 26 + j) * 16;
                    if (Raw[e] == 0xFF) continue;
                    int size = 128 << (Raw[e + 3] & 7);
                    Add(i, Raw[e], Raw[e + 1], Raw[e + 2], Raw[e + 3], ptr, size);
                    ptr += size;
                }
            }
        }

        void LoadNfd1()
        {
            Format = "NFD r1";
            WriteProtect = Raw[0x114] != 0;
            int ptr = (int)U32(Raw, 0x110);
            for (int i = 0; i < 164; i++)
            {
                int th = (int)U32(Raw, 0x120 + i * 4);
                if (th == 0) continue;
                if (th + 16 > Raw.Length) throw new Exception("The NFD track table is corrupted");
                int nsec = U16(Raw, th);
                int ndiag = U16(Raw, th + 2);
                int p = th + 16;
                for (int j = 0; j < nsec; j++, p += 16)
                {
                    int size = 128 << (Raw[p + 3] & 7);
                    Add(i, Raw[p], Raw[p + 1], Raw[p + 2], Raw[p + 3], ptr, size);
                    ptr += size * (1 + Raw[p + 10]);
                }
                for (int j = 0; j < ndiag; j++, p += 16)
                {
                    int len = (int)U32(Raw, p + 10);
                    ptr += len * (1 + Raw[p + 9]);
                }
            }
        }

        void LoadD88()
        {
            Format = "D88";
            WriteProtect = Raw[0x1A] != 0;
            int end = Math.Min(Raw.Length, (int)Math.Max(U32(Raw, 0x1C), 0x2B0));
            int tableEnd = 0x20 + 164 * 4;
            for (int i = 0; i < 164; i++)
            {
                int o = (int)U32(Raw, 0x20 + i * 4);
                if (o != 0 && o < tableEnd) tableEnd = o;
            }
            for (int i = 0; 0x20 + i * 4 < tableEnd && i < 164; i++)
            {
                int p = (int)U32(Raw, 0x20 + i * 4);
                if (p == 0 || p + 16 > end) continue;
                int nsec = U16(Raw, p + 4);
                for (int j = 0; j < nsec && p + 16 <= end; j++)
                {
                    int size = U16(Raw, p + 0x0E);
                    Add(i, Raw[p], Raw[p + 1], Raw[p + 2], Raw[p + 3], p + 16, size);
                    p += 16 + size;
                }
            }
        }

        void LoadRaw()
        {
            // size, cylinders, sectors/track, bytes/sector
            int[][] geo = {
                new int[] { 1261568, 77, 8, 1024 },  // 2HD 1.25MB
                new int[] { 1474560, 80, 18, 512 },  // 2HD 1.44MB
                new int[] { 1228800, 80, 15, 512 },  // 2HC 1.2MB
                new int[] { 737280, 80, 9, 512 },    // 2DD 720KB
                new int[] { 655360, 80, 8, 512 },    // 2DD 640KB
            };
            foreach (int[] g in geo)
            {
                if (Raw.Length != g[0]) continue;
                Format = g[0] == 1261568 ? "HDM" : "RAW";
                int n = g[3] == 1024 ? 3 : 2, off = 0;
                for (int t = 0; t < g[1] * 2; t++)
                    for (int s = 1; s <= g[2]; s++, off += g[3])
                        Add(t, t >> 1, t & 1, s, n, off, g[3]);
                return;
            }
            throw new Exception("Unsupported image format (NFD/D88/HDM only)");
        }

        public Sector Find(int track, int r, int size)
        {
            if (track < 0 || track >= Tracks.Length) return null;
            foreach (Sector s in Tracks[track])
                if (s.R == r && (size < 0 || s.Size == size)) return s;
            return null;
        }

        public void Save()
        {
            using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                fs.Write(Raw, 0, Raw.Length);
                fs.Flush(true);
            }
        }
    }

    // ---------------------------------------------------------------- FAT

    class Node
    {
        public string HostName;
        public byte[] Name = new byte[11];
        public byte[] Entry;        // 元のディレクトリエントリ(32バイト)。新規ならnull
        public byte Attr;
        public ushort Time, Date;
        public int Size;
        public List<int> Chain;     // nullなら要割り当て
        public byte[] Data;
        public List<Node> Children;
        public bool Reused;
        public bool NewData;        // 今回クラスタを割り当てたファイル
        public Node Old;            // 対応する変更前のノード
        public bool IsDir { get { return (Attr & 0x10) != 0; } }
    }

    class FatVolume
    {
        public DiskImage Img;
        public int Bps, Spc, Reserved, NFats, RootEntries, Total, FatSize, Spt, Heads;
        public int RootSec, RootSecs, DataSec, Clusters;
        public bool Fat16;
        public byte[] Vol;
        public int[] SectorOffset;
        public int[] Fat;
        public Node Root;
        public byte[] LabelEntry;
        public bool BackupDone;

        static readonly Encoding Sjis = Encoding.GetEncoding(932);
        static readonly Encoding SjisStrict = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        int Bpc { get { return Bps * Spc; } }
        int Eoc { get { return Fat16 ? 0xFFFF : 0xFFF; } }

        public static FatVolume Open(DiskImage img)
        {
            FatVolume v = new FatVolume();
            v.Img = img;
            Sector boot = img.Find(0, 1, -1);
            if (boot == null) throw new Exception("No boot sector found");
            byte[] b = new byte[boot.Size];
            Buffer.BlockCopy(img.Raw, boot.Offset, b, 0, boot.Size);
            if (!v.TryBpb(b, boot.Size) && !v.GuessBpb(boot.Size))
                throw new Exception("Not a PC-98 DOS disk (no valid BPB)");
            v.Build();
            return v;
        }

        bool TryBpb(byte[] b, int secSize)
        {
            if (b.Length < 32) return false;
            int bps = DiskImage.U16(b, 11), spc = b[13], res = DiskImage.U16(b, 14), nf = b[16];
            int root = DiskImage.U16(b, 17), tot = DiskImage.U16(b, 19), media = b[21];
            int fsz = DiskImage.U16(b, 22), spt = DiskImage.U16(b, 24), heads = DiskImage.U16(b, 26);
            if (tot == 0) tot = (int)DiskImage.U32(b, 32);
            if (bps != secSize || (bps != 256 && bps != 512 && bps != 1024 && bps != 2048)) return false;
            if (spc == 0 || (spc & (spc - 1)) != 0 || res < 1 || nf < 1 || nf > 2) return false;
            if (root == 0 || tot == 0 || fsz == 0 || media < 0xF0 || spt < 1 || spt > 26 || heads < 1 || heads > 2) return false;
            Bps = bps; Spc = spc; Reserved = res; NFats = nf; RootEntries = root; Total = tot; FatSize = fsz; Spt = spt; Heads = heads;
            return true;
        }

        // BPBの無い古いディスクはトラック構成から決め打ちする
        bool GuessBpb(int secSize)
        {
            int spt = 0;
            foreach (Sector s in Img.Tracks[2]) if (s.Size == secSize) spt = Math.Max(spt, s.R);
            int[][] t = {
                // bps, spt, spc, root, total, fatsz
                new int[] { 1024, 8, 1, 192, 1232, 2 },
                new int[] { 512, 18, 1, 224, 2880, 9 },
                new int[] { 512, 15, 1, 224, 2400, 7 },
                new int[] { 512, 9, 2, 112, 1440, 3 },
                new int[] { 512, 8, 2, 112, 1280, 2 },
            };
            foreach (int[] g in t)
            {
                if (g[0] != secSize || g[1] != spt) continue;
                Bps = g[0]; Spt = g[1]; Spc = g[2]; RootEntries = g[3]; Total = g[4]; FatSize = g[5];
                Reserved = 1; NFats = 2; Heads = 2;
                Sector f = Img.Find(0, 2, secSize);
                return f != null && Img.Raw[f.Offset] >= 0xF0 && Img.Raw[f.Offset + 1] == 0xFF;
            }
            return false;
        }

        void Build()
        {
            Vol = new byte[Total * Bps];
            SectorOffset = new int[Total];
            for (int lba = 0; lba < Total; lba++)
            {
                int t = lba / Spt;
                int track = (t / Heads) * 2 + t % Heads;
                Sector s = Img.Find(track, lba % Spt + 1, Bps);
                SectorOffset[lba] = s != null ? s.Offset : -1;
                if (s != null) Buffer.BlockCopy(Img.Raw, s.Offset, Vol, lba * Bps, Bps);
            }
            RootSec = Reserved + NFats * FatSize;
            RootSecs = (RootEntries * 32 + Bps - 1) / Bps;
            DataSec = RootSec + RootSecs;
            if (DataSec >= Total) throw new Exception("Invalid FAT layout");
            for (int s = 0; s < DataSec; s++)
                if (SectorOffset[s] < 0) throw new Exception("Sectors are missing from the FAT/directory area");
            Clusters = (Total - DataSec) / Spc;
            Fat16 = Clusters >= 4085;
            int maxByFat = Fat16 ? FatSize * Bps / 2 - 2 : FatSize * Bps * 2 / 3 - 2;
            if (Clusters > maxByFat) Clusters = maxByFat;
            Fat = new int[Clusters + 2];
            int fb = Reserved * Bps;
            for (int c = 0; c < Clusters + 2; c++) Fat[c] = GetFat(Vol, fb, c);

            Root = new Node();
            Root.Attr = 0x10;
            Root.Chain = new List<int>();
            Root.Children = new List<Node>();
            ParseDir(Vol, RootSec * Bps, RootSecs * Bps, Root, true, 0);
        }

        int GetFat(byte[] b, int bas, int c)
        {
            if (Fat16) return DiskImage.U16(b, bas + c * 2);
            int o = bas + c * 3 / 2;
            return (c & 1) == 0 ? (b[o] | (b[o + 1] & 0x0F) << 8) : (b[o] >> 4 | b[o + 1] << 4);
        }

        void SetFat(byte[] b, int bas, int c, int v)
        {
            if (Fat16) { b[bas + c * 2] = (byte)v; b[bas + c * 2 + 1] = (byte)(v >> 8); return; }
            int o = bas + c * 3 / 2;
            if ((c & 1) == 0) { b[o] = (byte)v; b[o + 1] = (byte)((b[o + 1] & 0xF0) | ((v >> 8) & 0x0F)); }
            else { b[o] = (byte)((b[o] & 0x0F) | ((v & 0x0F) << 4)); b[o + 1] = (byte)(v >> 4); }
        }

        List<int> ReadChain(int start)
        {
            List<int> l = new List<int>();
            int c = start;
            while (c >= 2 && c < Clusters + 2 && l.Count < Clusters)
            {
                l.Add(c);
                c = Fat[c];
            }
            return l;
        }

        byte[] ReadClusters(List<int> chain, int max)
        {
            int len = Math.Min(max, chain.Count * Bpc);
            byte[] d = new byte[len];
            for (int i = 0; i < chain.Count && i * Bpc < len; i++)
                Buffer.BlockCopy(Vol, ClusterOffset(chain[i]), d, i * Bpc, Math.Min(Bpc, len - i * Bpc));
            return d;
        }

        int ClusterOffset(int c) { return (DataSec + (c - 2) * Spc) * Bps; }

        void ParseDir(byte[] buf, int off, int len, Node dir, bool isRoot, int depth)
        {
            HashSet<string> hostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int p = off; p + 32 <= off + len; p += 32)
            {
                byte f = buf[p];
                if (f == 0x00) break;
                if (f == 0xE5 || f == 0x2E) continue;
                byte attr = buf[p + 11];
                if (attr == 0x0F) continue;
                if ((attr & 0x08) != 0)
                {
                    if (isRoot && LabelEntry == null) { LabelEntry = new byte[32]; Buffer.BlockCopy(buf, p, LabelEntry, 0, 32); }
                    continue;
                }
                Node n = new Node();
                Buffer.BlockCopy(buf, p, n.Name, 0, 11);
                if (n.Name[0] == 0x05) n.Name[0] = 0xE5;
                n.Entry = new byte[32];
                Buffer.BlockCopy(buf, p, n.Entry, 0, 32);
                n.Attr = attr;
                n.Time = (ushort)DiskImage.U16(buf, p + 22);
                n.Date = (ushort)DiskImage.U16(buf, p + 24);
                n.Size = (int)DiskImage.U32(buf, p + 28);
                int start = DiskImage.U16(buf, p + 26);
                n.Chain = ReadChain(start);
                n.HostName = UniqueHostName(HostNameOf(n.Name), hostNames);
                if (n.IsDir)
                {
                    n.Children = new List<Node>();
                    n.Size = 0;
                    if (depth < 32)
                    {
                        byte[] d = ReadClusters(n.Chain, int.MaxValue);
                        ParseDir(d, 0, d.Length, n, false, depth + 1);
                    }
                }
                else
                {
                    n.Data = ReadClusters(n.Chain, n.Size);
                    n.Size = n.Data.Length;
                }
                dir.Children.Add(n);
            }
        }

        static string HostNameOf(byte[] name)
        {
            string b = Sjis.GetString(name, 0, 8).TrimEnd(' ');
            string e = Sjis.GetString(name, 8, 3).TrimEnd(' ');
            string s = e.Length > 0 ? b + "." + e : b;
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char ch in s) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
            s = sb.ToString();
            if (s.Length == 0 || s.EndsWith(".") || s.EndsWith(" ")) s += "_";
            string upBase = b.ToUpperInvariant();
            string[] dev = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
            if (Array.IndexOf(dev, upBase) >= 0) s = "_" + s;
            return s;
        }

        static string UniqueHostName(string s, HashSet<string> used)
        {
            string r = s;
            for (int i = 2; used.Contains(r); i++) r = s + "_" + i;
            used.Add(r);
            return r;
        }

        public static DateTime DosToDateTime(ushort date, ushort time)
        {
            try { return new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2); }
            catch { return new DateTime(1980, 1, 1); }
        }

        static void DateTimeToDos(DateTime t, out ushort date, out ushort time)
        {
            if (t.Year < 1980) t = new DateTime(1980, 1, 1);
            if (t.Year > 2107) t = new DateTime(2107, 12, 31, 23, 59, 58);
            date = (ushort)((t.Year - 1980) << 9 | t.Month << 5 | t.Day);
            time = (ushort)(t.Hour << 11 | t.Minute << 5 | t.Second / 2);
        }

        public long FreeBytes
        {
            get
            {
                long n = 0;
                for (int c = 2; c < Clusters + 2; c++) if (Fat[c] == 0) n++;
                return n * Bpc;
            }
        }

        public string MediaName
        {
            get
            {
                long sz = (long)Total * Bps;
                if (Bps == 1024 && Spt == 8) return "2HD 1.25MB";
                if (sz == 1474560) return "2HD 1.44MB";
                if (sz == 1228800) return "2HC 1.2MB";
                if (sz == 737280) return "2DD 720KB";
                if (sz == 655360) return "2DD 640KB";
                return (sz / 1024) + "KB";
            }
        }

        public string LabelText
        {
            get { return LabelEntry == null ? "" : Sjis.GetString(LabelEntry, 0, 11).TrimEnd(' '); }
        }

        // ------------------------------------------------ 展開

        public void Extract(string dir)
        {
            ExtractDir(Root, dir);
        }

        static FileAttributes WinAttr(byte a)
        {
            FileAttributes w = 0;
            if ((a & 0x01) != 0) w |= FileAttributes.ReadOnly;
            if ((a & 0x02) != 0) w |= FileAttributes.Hidden;
            if ((a & 0x04) != 0) w |= FileAttributes.System;
            if ((a & 0x20) != 0) w |= FileAttributes.Archive;
            return w;
        }

        void ExtractDir(Node dir, string path)
        {
            foreach (Node n in dir.Children)
            {
                string p = Path.Combine(path, n.HostName);
                DateTime t = DosToDateTime(n.Date, n.Time);
                if (n.IsDir)
                {
                    Directory.CreateDirectory(p);
                    ExtractDir(n, p);
                    Directory.SetLastWriteTime(p, t);
                    FileAttributes w = WinAttr((byte)(n.Attr & 0x07));
                    if (w != 0) File.SetAttributes(p, FileAttributes.Directory | w);
                }
                else
                {
                    File.WriteAllBytes(p, n.Data);
                    File.SetLastWriteTime(p, t);
                    FileAttributes w = WinAttr(n.Attr);
                    File.SetAttributes(p, w == 0 ? FileAttributes.Normal : w);
                }
            }
        }

        // ------------------------------------------------ 書き戻し

        // フォルダの内容をイメージへ反映する。変更があればtrue。
        // 容量不足などで反映できないときは例外(イメージは変更しない)。
        public bool Sync(string hostRoot, List<string> log)
        {
            Node nr = new Node();
            nr.Attr = 0x10;
            nr.Chain = new List<int>();
            nr.Children = new List<Node>();
            nr.Old = Root;
            BuildDir(hostRoot, Root, nr, "", log);

            int rootUse = nr.Children.Count + (LabelEntry != null ? 1 : 0);
            if (rootUse > RootEntries)
                throw new Exception("Too many files in the root directory (max " + (RootEntries - (LabelEntry != null ? 1 : 0)) + ")");

            int[] nf = (int[])Fat.Clone();
            FreeUnused(Root, nf);

            long need = Need(nr, true);
            long free = 0;
            for (int c = 2; c < Clusters + 2; c++) if (nf[c] == 0) free++;
            if (need > free)
                throw new Exception("Not enough space on the disk (" + ((need - free) * Bpc).ToString("#,0") + " more bytes needed)");

            Allocate(nr, true, nf);

            byte[] nv = (byte[])Vol.Clone();
            WriteDir(nr, true, 0, nv);
            for (int k = 0; k < NFats; k++)
            {
                int bas = (Reserved + k * FatSize) * Bps;
                for (int c = 2; c < Clusters + 2; c++) SetFat(nv, bas, c, nf[c]);
            }

            List<int> dirty = new List<int>();
            for (int s = 0; s < Total; s++)
            {
                int o = s * Bps;
                for (int i = 0; i < Bps; i++)
                {
                    if (nv[o + i] != Vol[o + i]) { dirty.Add(s); break; }
                }
            }
            if (dirty.Count == 0)
            {
                Root = nr;
                Fat = nf;
                return false;
            }
            foreach (int s in dirty)
                if (SectorOffset[s] < 0) throw new Exception("Sector " + s + " is missing from the image and cannot be written");

            if (!BackupDone)
            {
                File.Copy(Img.FilePath, Img.FilePath + ".bak", true);
                BackupDone = true;
            }
            foreach (int s in dirty) Buffer.BlockCopy(nv, s * Bps, Img.Raw, SectorOffset[s], Bps);
            Img.Save();
            Vol = nv;
            Fat = nf;
            Root = nr;
            log.Add("Wrote " + dirty.Count + " sectors");
            return true;
        }

        static byte[] ReadHostFile(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] d = new byte[fs.Length];
                    int p = 0;
                    while (p < d.Length)
                    {
                        int r = fs.Read(d, p, d.Length - p);
                        if (r <= 0) break;
                        p += r;
                    }
                    if (p != d.Length) Array.Resize(ref d, p);
                    return d;
                }
            }
            catch (IOException) { throw new RetryException(Path.GetFileName(path) + " is in use"); }
            catch (UnauthorizedAccessException) { throw new RetryException("Cannot read " + Path.GetFileName(path)); }
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        void BuildDir(string hostDir, Node oldDir, Node nd, string rel, List<string> log)
        {
            FileSystemInfo[] items = new DirectoryInfo(hostDir).GetFileSystemInfos();
            Dictionary<string, FileSystemInfo> byName = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (FileSystemInfo fi in items)
            {
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                byName[fi.Name] = fi;
            }

            // 既存エントリは元の並び順のまま、新規は後ろに名前順で追加
            List<KeyValuePair<FileSystemInfo, Node>> order = new List<KeyValuePair<FileSystemInfo, Node>>();
            HashSet<string> used = new HashSet<string>();
            if (oldDir != null)
            {
                foreach (Node o in oldDir.Children)
                {
                    FileSystemInfo fi;
                    if (!byName.TryGetValue(o.HostName, out fi)) continue;
                    bool isDir = (fi.Attributes & FileAttributes.Directory) != 0;
                    if (isDir != o.IsDir) continue;
                    order.Add(new KeyValuePair<FileSystemInfo, Node>(fi, o));
                    byName.Remove(o.HostName);
                    used.Add(Latin1.GetString(o.Name));
                }
            }
            List<FileSystemInfo> rest = new List<FileSystemInfo>(byName.Values);
            rest.Sort(delegate(FileSystemInfo a, FileSystemInfo b) { return string.CompareOrdinal(a.Name.ToUpperInvariant(), b.Name.ToUpperInvariant()); });
            foreach (FileSystemInfo fi in rest)
            {
                string lower = fi.Name.ToLowerInvariant();
                if (lower == "desktop.ini" || lower == "thumbs.db") continue;
                order.Add(new KeyValuePair<FileSystemInfo, Node>(fi, null));
            }

            foreach (KeyValuePair<FileSystemInfo, Node> kv in order)
            {
                FileSystemInfo fi = kv.Key;
                Node old = kv.Value;
                bool isDir = (fi.Attributes & FileAttributes.Directory) != 0;
                Node n = new Node();
                n.HostName = fi.Name;
                if (old != null)
                {
                    n.Name = old.Name;
                    n.Entry = old.Entry;
                    n.Old = old;
                }
                else
                {
                    bool alias;
                    n.Name = MakeShortName(fi.Name, used, out alias);
                    if (alias) log.Add("Renamed to 8.3: " + rel + fi.Name + " → " + rel + HostNameOf(n.Name));
                }
                used.Add(Latin1.GetString(n.Name));

                byte a = 0;
                FileAttributes w = fi.Attributes;
                if ((w & FileAttributes.ReadOnly) != 0) a |= 0x01;
                if ((w & FileAttributes.Hidden) != 0) a |= 0x02;
                if ((w & FileAttributes.System) != 0) a |= 0x04;

                if (isDir)
                {
                    n.Attr = (byte)(a | 0x10);
                    n.Children = new List<Node>();
                    if (old != null)
                    {
                        n.Time = old.Time; n.Date = old.Date;
                        n.Chain = new List<int>(old.Chain);
                        old.Reused = true;
                    }
                    else
                    {
                        DateTimeToDos(fi.LastWriteTime, out n.Date, out n.Time);
                    }
                    BuildDir(fi.FullName, old, n, rel + HostNameOf(n.Name) + "\\", log);
                }
                else
                {
                    if ((w & FileAttributes.Archive) != 0) a |= 0x20;
                    n.Attr = a;
                    if (((FileInfo)fi).Length > (long)Clusters * Bpc)
                        throw new Exception("Not enough space on the disk (" + fi.Name + ")");
                    n.Data = ReadHostFile(fi.FullName);
                    n.Size = n.Data.Length;
                    DateTimeToDos(fi.LastWriteTime, out n.Date, out n.Time);
                    int needC = (n.Size + Bpc - 1) / Bpc;
                    if (old != null && Same(old.Data, n.Data) && old.Chain.Count == needC)
                    {
                        n.Chain = old.Chain;
                        old.Reused = true;
                    }
                }
                nd.Children.Add(n);
            }
        }

        void FreeUnused(Node dir, int[] nf)
        {
            foreach (Node n in dir.Children)
            {
                if (!n.Reused)
                    foreach (int c in n.Chain) nf[c] = 0;
                if (n.IsDir) FreeUnused(n, nf);
            }
        }

        int DirClusters(Node d)
        {
            int bytes = (d.Children.Count + 2) * 32;
            return Math.Max(1, (bytes + Bpc - 1) / Bpc);
        }

        long Need(Node dir, bool isRoot)
        {
            long need = 0;
            if (!isRoot)
            {
                int c = DirClusters(dir);
                need += dir.Chain == null ? c : Math.Max(0, c - dir.Chain.Count);
            }
            foreach (Node n in dir.Children)
            {
                if (n.IsDir) need += Need(n, false);
                else if (n.Chain == null) need += (n.Size + Bpc - 1) / Bpc;
            }
            return need;
        }

        List<int> Alloc(int count, int[] nf)
        {
            List<int> l = new List<int>();
            if (count <= 0) return l;
            int run = 0, start = 0;
            for (int c = 2; c < Clusters + 2; c++)
            {
                if (nf[c] != 0) { run = 0; continue; }
                if (run == 0) start = c;
                if (++run == count)
                {
                    for (int i = 0; i < count; i++) l.Add(start + i);
                    break;
                }
            }
            if (l.Count == 0)
                for (int c = 2; c < Clusters + 2 && l.Count < count; c++)
                    if (nf[c] == 0) l.Add(c);
            for (int i = 0; i < l.Count; i++) nf[l[i]] = i + 1 < l.Count ? l[i + 1] : Eoc;
            return l;
        }

        void Allocate(Node dir, bool isRoot, int[] nf)
        {
            if (!isRoot)
            {
                int c = DirClusters(dir);
                if (dir.Chain == null) dir.Chain = Alloc(c, nf);
                else if (dir.Chain.Count < c)
                {
                    List<int> ext = Alloc(c - dir.Chain.Count, nf);
                    if (dir.Chain.Count > 0) nf[dir.Chain[dir.Chain.Count - 1]] = ext[0];
                    dir.Chain.AddRange(ext);
                }
            }
            foreach (Node n in dir.Children)
            {
                if (n.IsDir) continue;
                if (n.Chain == null) { n.Chain = Alloc((n.Size + Bpc - 1) / Bpc, nf); n.NewData = true; }
            }
            foreach (Node n in dir.Children)
                if (n.IsDir) Allocate(n, false, nf);
        }

        byte[] MakeEntry(Node n)
        {
            byte[] e = n.Entry != null ? (byte[])n.Entry.Clone() : new byte[32];
            Buffer.BlockCopy(n.Name, 0, e, 0, 11);
            if (e[0] == 0xE5) e[0] = 0x05;
            e[11] = n.Attr;
            e[20] = 0; e[21] = 0;
            e[22] = (byte)n.Time; e[23] = (byte)(n.Time >> 8);
            e[24] = (byte)n.Date; e[25] = (byte)(n.Date >> 8);
            int start = n.Chain.Count > 0 ? n.Chain[0] : 0;
            e[26] = (byte)start; e[27] = (byte)(start >> 8);
            int size = n.IsDir ? 0 : n.Size;
            e[28] = (byte)size; e[29] = (byte)(size >> 8); e[30] = (byte)(size >> 16); e[31] = (byte)(size >> 24);
            return e;
        }

        static byte[] DotEntry(string name, Node d, int cluster)
        {
            byte[] e = new byte[32];
            for (int i = 0; i < 11; i++) e[i] = (byte)(i < name.Length ? name[i] : ' ');
            e[11] = 0x10;
            e[22] = (byte)d.Time; e[23] = (byte)(d.Time >> 8);
            e[24] = (byte)d.Date; e[25] = (byte)(d.Date >> 8);
            e[26] = (byte)cluster; e[27] = (byte)(cluster >> 8);
            return e;
        }

        void WriteChain(List<int> chain, byte[] data, byte[] nv)
        {
            for (int i = 0; i < chain.Count; i++)
            {
                int o = ClusterOffset(chain[i]);
                int src = i * Bpc;
                int len = Math.Max(0, Math.Min(Bpc, data.Length - src));
                if (len > 0) Buffer.BlockCopy(data, src, nv, o, len);
                if (len < Bpc) Array.Clear(nv, o + len, Bpc - len);
            }
        }

        void WriteDir(Node dir, bool isRoot, int parentCluster, byte[] nv)
        {
            MemoryStream ms = new MemoryStream();
            if (isRoot)
            {
                if (LabelEntry != null) ms.Write(LabelEntry, 0, 32);
            }
            else
            {
                ms.Write(DotEntry(".", dir, dir.Chain[0]), 0, 32);
                ms.Write(DotEntry("..", dir, parentCluster), 0, 32);
            }
            foreach (Node n in dir.Children) ms.Write(MakeEntry(n), 0, 32);
            byte[] ents = ms.ToArray();

            // 中身が変わっていないディレクトリには触れない(削除済みエントリ等もそのまま残す)
            bool same = dir.Old != null && dir.Old.Children.Count == dir.Children.Count &&
                        (isRoot || SameChain(dir.Old.Chain, dir.Chain));
            for (int i = 0; same && i < dir.Children.Count; i++)
                same = Same(MakeEntry(dir.Old.Children[i]), MakeEntry(dir.Children[i]));

            if (same) { }
            else if (isRoot)
            {
                int o = RootSec * Bps;
                Array.Clear(nv, o, RootSecs * Bps);
                Buffer.BlockCopy(ents, 0, nv, o, ents.Length);
            }
            else WriteChain(dir.Chain, ents, nv);

            foreach (Node n in dir.Children)
            {
                if (n.IsDir) WriteDir(n, false, isRoot ? 0 : dir.Chain[0], nv);
                else if (n.NewData) WriteChain(n.Chain, n.Data, nv);
            }
        }

        static bool SameChain(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ------------------------------------------------ 8.3名

        const string BadChars = "\"*+,./:;<=>?[\\]|";

        static string UpperAscii(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c >= 'a' && c <= 'z' ? (char)(c - 32) : c);
            return sb.ToString();
        }

        static bool ValidPart(string s, int max, out byte[] bytes)
        {
            bytes = null;
            foreach (char ch in s)
                if (ch < 0x21 || BadChars.IndexOf(ch) >= 0) return false;
            try { bytes = SjisStrict.GetBytes(s); }
            catch { return false; }
            return bytes.Length <= max;
        }

        static string Clean(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch <= 0x20 || ch == '.') continue;
                if (BadChars.IndexOf(ch) >= 0) { sb.Append('_'); continue; }
                try { SjisStrict.GetBytes(ch.ToString()); sb.Append(ch); }
                catch { }
            }
            return sb.ToString();
        }

        static byte[] TruncBytes(string s, int max)
        {
            List<byte> l = new List<byte>();
            foreach (char ch in s)
            {
                byte[] b = SjisStrict.GetBytes(ch.ToString());
                if (l.Count + b.Length > max) break;
                l.AddRange(b);
            }
            return l.ToArray();
        }

        static byte[] Pack(byte[] b, byte[] e)
        {
            byte[] n = new byte[11];
            for (int i = 0; i < 11; i++) n[i] = 0x20;
            Buffer.BlockCopy(b, 0, n, 0, b.Length);
            Buffer.BlockCopy(e, 0, n, 8, e.Length);
            return n;
        }

        public static byte[] MakeShortName(string host, HashSet<string> used, out bool alias)
        {
            string up = UpperAscii(host);
            int dot = up.LastIndexOf('.');
            string b = dot >= 0 ? up.Substring(0, dot) : up;
            string e = dot >= 0 ? up.Substring(dot + 1) : "";
            byte[] bb, eb = new byte[0];
            if (b.Length > 0 && ValidPart(b, 8, out bb) && (e.Length == 0 || ValidPart(e, 3, out eb)))
            {
                byte[] n = Pack(bb, eb);
                if (!used.Contains(Latin1.GetString(n))) { alias = false; return n; }
            }
            alias = true;
            string cb = Clean(b);
            if (cb.Length == 0) cb = "_";
            byte[] ceb = TruncBytes(Clean(e), 3);
            for (int i = 1; ; i++)
            {
                string suf = "~" + i;
                byte[] head = TruncBytes(cb, 8 - suf.Length);
                byte[] nb = new byte[head.Length + suf.Length];
                Buffer.BlockCopy(head, 0, nb, 0, head.Length);
                Encoding.ASCII.GetBytes(suf, 0, suf.Length, nb, head.Length);
                byte[] n = Pack(nb, ceb);
                if (!used.Contains(Latin1.GetString(n))) return n;
            }
        }
    }

    // ---------------------------------------------------------------- ドライブ割り当て

    static class Native
    {
        public const int DDD_REMOVE_DEFINITION = 2;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DefineDosDevice(int flags, string device, string target);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int QueryDosDevice(string device, StringBuilder buf, int max);
        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int pid);

        public static string QueryTarget(char letter)
        {
            StringBuilder sb = new StringBuilder(1024);
            return QueryDosDevice(letter + ":", sb, sb.Capacity) > 0 ? sb.ToString() : null;
        }

        public static void NotifyDrive(char letter, bool added)
        {
            IntPtr p = Marshal.StringToHGlobalUni(letter + ":\\");
            SHChangeNotify(added ? 0x100 : 0x80, 0x0005, p, IntPtr.Zero);
            Marshal.FreeHGlobal(p);
        }
    }

    class Mount
    {
        public static readonly string BaseDir = Path.Combine(Path.GetTempPath(), "DiskMount98");
        const string LabelKey = @"Software\Classes\Applications\Explorer.exe\Drives\";

        public string ImagePath;
        public string Dir;
        public char Letter;
        public FatVolume Vol;
        public bool ReadOnly;
        FileStream lockFile;
        bool labelCreated, defined;

        static string IdOf(string path)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
                return BitConverter.ToString(h, 0, 8).Replace("-", "");
            }
        }

        // 異常終了で残った一時フォルダとドライブ割り当てを掃除する
        public static void CleanupStale()
        {
            if (!Directory.Exists(BaseDir)) return;
            foreach (string d in Directory.GetDirectories(BaseDir))
            {
                string lk = d + ".lock";
                try { if (File.Exists(lk)) File.Delete(lk); }
                catch { continue; }
                for (char c = 'D'; c <= 'Z'; c++)
                {
                    string t = Native.QueryTarget(c);
                    if (t != null && t.TrimEnd('\\').EndsWith(d, StringComparison.OrdinalIgnoreCase))
                        Native.DefineDosDevice(Native.DDD_REMOVE_DEFINITION, c + ":", null);
                }
                DeleteTree(d);
            }
        }

        static void DeleteTree(string d)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (!Directory.Exists(d)) return;
                    foreach (string f in Directory.GetFileSystemEntries(d, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, (File.GetAttributes(f) & FileAttributes.Directory) != 0 ? FileAttributes.Directory : FileAttributes.Normal);
                    Directory.Delete(d, true);
                    return;
                }
                catch { System.Threading.Thread.Sleep(300); }
            }
        }

        // 既に同じイメージをマウント中ならそのドライブ文字を返す
        public static char AlreadyMounted(string image)
        {
            string lk = Path.Combine(BaseDir, IdOf(image)) + ".lock";
            if (!File.Exists(lk)) return '\0';
            try
            {
                using (FileStream fs = new FileStream(lk, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return '\0';
            }
            catch (IOException) { }
            try
            {
                using (FileStream fs = new FileStream(lk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs))
                {
                    string s = sr.ReadToEnd().Trim();
                    return s.Length > 0 ? s[0] : '?';
                }
            }
            catch { return '?'; }
        }

        public void Open(string image)
        {
            ImagePath = Path.GetFullPath(image);
            DiskImage img = DiskImage.Load(ImagePath);
            Vol = FatVolume.Open(img);
            ReadOnly = img.WriteProtect;

            Directory.CreateDirectory(BaseDir);
            Dir = Path.Combine(BaseDir, IdOf(ImagePath));
            lockFile = new FileStream(Dir + ".lock", FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            DeleteTree(Dir);
            Directory.CreateDirectory(Dir);
            Vol.Extract(Dir);

            Letter = '\0';
            for (char c = 'Z'; c >= 'D'; c--)
            {
                if (Directory.Exists(c + ":\\") || Native.QueryTarget(c) != null) continue;
                Letter = c;
                break;
            }
            if (Letter == '\0') throw new Exception("No free drive letter");

            string label = Path.GetFileName(ImagePath);
            try
            {
                string k = LabelKey + Letter;
                // 他で設定済みのキーには触れない。自分の目印付きなら異常終了の残骸なので上書きする
                using (RegistryKey ex = Registry.CurrentUser.OpenSubKey(k))
                    labelCreated = ex == null || ex.GetValue("DiskMount98") != null;
                if (labelCreated)
                {
                    using (RegistryKey r = Registry.CurrentUser.CreateSubKey(k)) r.SetValue("DiskMount98", 1);
                    using (RegistryKey r = Registry.CurrentUser.CreateSubKey(k + @"\DefaultLabel")) r.SetValue("", label);
                    using (RegistryKey r = Registry.CurrentUser.CreateSubKey(k + @"\DefaultIcon")) r.SetValue("", "\"" + Application.ExecutablePath + "\",0");
                }
            }
            catch { labelCreated = false; }

            if (!Native.DefineDosDevice(0, Letter + ":", Dir))
                throw new Exception("Cannot assign drive " + Letter + ":");
            defined = true;
            Native.NotifyDrive(Letter, true);

            byte[] lb = Encoding.ASCII.GetBytes(Letter.ToString());
            lockFile.Write(lb, 0, lb.Length);
            lockFile.Flush();
        }

        public void Close()
        {
            if (defined)
            {
                defined = false;
                Native.DefineDosDevice(Native.DDD_REMOVE_DEFINITION, Letter + ":", null);
                Native.NotifyDrive(Letter, false);
            }
            if (labelCreated)
            {
                labelCreated = false;
                try { Registry.CurrentUser.DeleteSubKeyTree(LabelKey + Letter); } catch { }
            }
            if (Dir != null) DeleteTree(Dir);
            if (lockFile != null)
            {
                lockFile.Dispose();
                try { File.Delete(Dir + ".lock"); } catch { }
            }
        }
    }

    // ---------------------------------------------------------------- 画面

    class MainForm : Form
    {
        Mount m;
        Label lblDrive, lblInfo, lblStatus;
        TextBox log;
        Button bOpen, bEject;
        Timer timer;
        FileSystemWatcher fsw;
        bool pending;
        bool closing;

        public MainForm(Mount mount)
        {
            m = mount;
            Text = "DiskMount98 - " + Path.GetFileName(m.ImagePath);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(520, 300);
            MinimumSize = new Size(420, 260);
            StartPosition = FormStartPosition.CenterScreen;

            TableLayoutPanel tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.Padding = new Padding(10);
            tl.ColumnCount = 1;
            tl.RowCount = 5;
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(tl);

            lblDrive = new Label();
            lblDrive.AutoSize = true;
            lblDrive.Font = new Font(Font.FontFamily, Font.Size * 1.6f, FontStyle.Bold);
            lblDrive.Text = m.Letter + ":  " + Path.GetFileName(m.ImagePath);
            tl.Controls.Add(lblDrive);

            lblInfo = new Label();
            lblInfo.AutoSize = true;
            lblInfo.Margin = new Padding(3, 2, 3, 8);
            tl.Controls.Add(lblInfo);

            log = new TextBox();
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Dock = DockStyle.Fill;
            tl.Controls.Add(log);

            lblStatus = new Label();
            lblStatus.AutoSize = true;
            lblStatus.Margin = new Padding(3, 6, 3, 6);
            tl.Controls.Add(lblStatus);

            FlowLayoutPanel bp = new FlowLayoutPanel();
            bp.FlowDirection = FlowDirection.RightToLeft;
            bp.Dock = DockStyle.Fill;
            bp.AutoSize = true;
            bEject = MakeButton("Unmount", delegate { Close(); });
            bOpen = MakeButton("Open in Explorer", delegate { OpenExplorer(); });
            bp.Controls.Add(bEject);
            bp.Controls.Add(bOpen);
            tl.Controls.Add(bp);

            UpdateInfo();
            if (m.ReadOnly)
            {
                SetStatus("Read-only image. Changes will not be written back.", Color.DarkOrange);
            }
            else SetStatus("Changes are written back to the image automatically.", SystemColors.ControlText);
            AddLog("Mounted " + m.Vol.Img.Format + " on " + m.Letter + ":");

            timer = new Timer();
            timer.Interval = 1500;
            timer.Tick += delegate { timer.Stop(); DoSync(); };

            if (!m.ReadOnly)
            {
                fsw = new FileSystemWatcher(m.Dir);
                fsw.IncludeSubdirectories = true;
                fsw.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes;
                fsw.SynchronizingObject = this;
                FileSystemEventHandler h = delegate { Changed(); };
                fsw.Changed += h;
                fsw.Created += h;
                fsw.Deleted += h;
                fsw.Renamed += delegate { Changed(); };
                fsw.Error += delegate { Changed(); };
                fsw.EnableRaisingEvents = true;
            }
            Shown += delegate { OpenExplorer(); };
        }

        Button MakeButton(string text, EventHandler h)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Padding = new Padding(6, 2, 6, 2);
            b.Click += h;
            return b;
        }

        void OpenExplorer()
        {
            try { Process.Start("explorer.exe", m.Letter + ":\\"); } catch { }
        }

        void UpdateInfo()
        {
            FatVolume v = m.Vol;
            string s = v.Img.Format + "  /  " + v.MediaName + "  /  " + (v.Fat16 ? "FAT16" : "FAT12") +
                "  /  " + v.FreeBytes.ToString("#,0") + " bytes free";
            if (v.LabelText.Length > 0) s += "  /  Label " + v.LabelText;
            lblInfo.Text = s;
        }

        void SetStatus(string s, Color c)
        {
            lblStatus.Text = s;
            lblStatus.ForeColor = c;
        }

        void AddLog(string s)
        {
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n");
        }

        void Changed()
        {
            if (closing) return;
            pending = true;
            SetStatus("Change detected...", SystemColors.ControlText);
            timer.Stop();
            timer.Interval = 1500;
            timer.Start();
        }

        // 成功(変更なし含む)ならnull、失敗ならエラーメッセージ
        string DoSync()
        {
            if (m.ReadOnly) return null;
            List<string> msgs = new List<string>();
            try
            {
                pending = false;
                bool changed = m.Vol.Sync(m.Dir, msgs);
                foreach (string s in msgs) AddLog(s);
                UpdateInfo();
                if (changed) SetStatus("Written to image (" + DateTime.Now.ToString("HH:mm:ss") + ")", Color.DarkGreen);
                else SetStatus("Image is up to date.", SystemColors.ControlText);
                return null;
            }
            catch (RetryException ex)
            {
                pending = true;
                SetStatus(ex.Message + ". Retrying shortly...", SystemColors.ControlText);
                if (!closing) { timer.Interval = 2000; timer.Start(); }
                return ex.Message;
            }
            catch (Exception ex)
            {
                pending = true;
                foreach (string s in msgs) AddLog(s);
                AddLog("Error: " + ex.Message);
                SetStatus("Cannot write: " + ex.Message, Color.Red);
                return ex.Message;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            closing = true;
            string err = null;
            if (!m.ReadOnly)
            {
                for (int i = 0; i < 6; i++)
                {
                    err = DoSync();
                    if (err == null || !pending) break;
                    System.Threading.Thread.Sleep(500);
                }
            }
            if (err != null && e.CloseReason == CloseReason.UserClosing)
            {
                DialogResult r = MessageBox.Show(this,
                    "Some changes could not be written back to the image.\n\n" + err + "\n\nDiscard them and unmount?",
                    "DiskMount98", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes)
                {
                    e.Cancel = true;
                    closing = false;
                    return;
                }
            }
            if (fsw != null) fsw.Dispose();
            m.Close();
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // テスト用: /extract <img> <dir>, /sync <img> <dir>
            if (args.Length == 3 && (args[0] == "/extract" || args[0] == "/sync")) return Cli(args);

            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Mount.CleanupStale(); } catch { }

            List<string> files = new List<string>();
            foreach (string a in args) if (File.Exists(a)) files.Add(a);
            if (files.Count == 0)
            {
                OpenFileDialog dlg = new OpenFileDialog();
                dlg.Title = "Select a disk image to mount";
                dlg.Filter = "PC-98 disk images (*.nfd;*.d88;*.d98;*.88d;*.hdm)|*.nfd;*.d88;*.d98;*.88d;*.hdm|All files (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() != DialogResult.OK) return 0;
                files.AddRange(dlg.FileNames);
            }
            for (int i = 1; i < files.Count; i++)
                Process.Start(Application.ExecutablePath, "\"" + files[i] + "\"");

            string image = files[0];
            char cur = Mount.AlreadyMounted(image);
            if (cur != '\0')
            {
                if (cur != '?') Process.Start("explorer.exe", cur + ":\\");
                return 0;
            }

            Mount m = new Mount();
            try { m.Open(image); }
            catch (Exception ex)
            {
                m.Close();
                MessageBox.Show(Path.GetFileName(image) + "\n\n" + ex.Message, "DiskMount98", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            Application.Run(new MainForm(m));
            return 0;
        }

        static int Cli(string[] args)
        {
            Native.AttachConsole(-1);
            try
            {
                FatVolume v = FatVolume.Open(DiskImage.Load(args[1]));
                Console.WriteLine(v.Img.Format + " " + v.MediaName + (v.Fat16 ? " FAT16" : " FAT12") + " free=" + v.FreeBytes);
                if (args[0] == "/extract")
                {
                    Directory.CreateDirectory(args[2]);
                    v.Extract(args[2]);
                }
                else
                {
                    List<string> log = new List<string>();
                    bool changed = v.Sync(args[2], log);
                    foreach (string s in log) Console.WriteLine(s);
                    Console.WriteLine(changed ? "changed free=" + v.FreeBytes : "unchanged");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }
    }
}
