using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace CoreScope.Core.Hardware;

/// <summary>Reads the CPUID instruction directly: identity, instruction-set extensions and cache layout.</summary>
public static class CpuId
{
    public static bool IsSupported => X86Base.IsSupported;

    private static (uint A, uint B, uint C, uint D) Leaf(uint leaf, uint subLeaf = 0)
    {
        var (a, b, c, d) = X86Base.CpuId(unchecked((int)leaf), unchecked((int)subLeaf));
        return (unchecked((uint)a), unchecked((uint)b), unchecked((uint)c), unchecked((uint)d));
    }

    private static bool Bit(uint value, int bit) => (value & (1u << bit)) != 0;

    public static void Fill(CpuSpec cpu)
    {
        if (!IsSupported) return;

        var (maxLeaf, vb, vc, vd) = Leaf(0);
        var vendor = Ascii(vb, vd, vc);
        cpu.Vendor = vendor switch
        {
            "GenuineIntel" => "Intel",
            "AuthenticAMD" => "AMD",
            _ => vendor,
        };

        uint maxExt = Leaf(0x80000000).A;
        if (maxExt >= 0x80000004)
        {
            var brand = new StringBuilder();
            for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
            {
                var (a, b, c, d) = Leaf(leaf);
                brand.Append(Ascii(a, b, c, d));
            }
            var name = brand.ToString().Trim('\0', ' ');
            if (name.Length > 0) cpu.Name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ");
        }

        var (sig, _, ecx1, edx1) = Leaf(1);
        int stepping = (int)(sig & 0xF);
        int model = (int)((sig >> 4) & 0xF);
        int family = (int)((sig >> 8) & 0xF);
        int extModel = (int)((sig >> 16) & 0xF);
        int extFamily = (int)((sig >> 20) & 0xFF);
        cpu.Family = family == 0xF ? family + extFamily : family;
        cpu.Model = family is 0x6 or 0xF ? (extModel << 4) + model : model;
        cpu.Stepping = stepping;

        uint ebx7 = 0, ecx7 = 0, edx7 = 0, eax7s1 = 0;
        if (maxLeaf >= 7)
        {
            (_, ebx7, ecx7, edx7) = Leaf(7);
            eax7s1 = Leaf(7, 1).A;
        }
        uint ecxExt = 0, edxExt = 0;
        if (maxExt >= 0x80000001) (_, _, ecxExt, edxExt) = Leaf(0x80000001);

        var f = cpu.Features;
        void Add(bool present, string name) { if (present) f.Add(name); }
        Add(Bit(edx1, 23), "MMX");
        Add(Bit(edx1, 25), "SSE");
        Add(Bit(edx1, 26), "SSE2");
        Add(Bit(ecx1, 0), "SSE3");
        Add(Bit(ecx1, 9), "SSSE3");
        Add(Bit(ecx1, 19), "SSE4.1");
        Add(Bit(ecx1, 20), "SSE4.2");
        Add(Bit(ecxExt, 6), "SSE4A");
        Add(Bit(edxExt, 29), "x86-64");
        Add(Bit(ecx1, 5), "VT-x");
        Add(Bit(ecxExt, 2), "AMD-V");
        Add(Bit(ecx1, 25), "AES");
        Add(Bit(ecx1, 1), "PCLMULQDQ");
        Add(Bit(ebx7, 29), "SHA");
        Add(Bit(ecx1, 28), "AVX");
        Add(Bit(ebx7, 5), "AVX2");
        Add(Bit(eax7s1, 4), "AVX-VNNI");
        Add(Bit(ebx7, 16), "AVX-512F");
        Add(Bit(ebx7, 17), "AVX-512DQ");
        Add(Bit(ebx7, 30), "AVX-512BW");
        Add(Bit(ebx7, 31), "AVX-512VL");
        Add(Bit(ecx7, 11), "AVX-512 VNNI");
        Add(Bit(ecx1, 12), "FMA3");
        Add(Bit(ecx1, 29), "F16C");
        Add(Bit(ebx7, 3), "BMI1");
        Add(Bit(ebx7, 8), "BMI2");
        Add(Bit(ebx7, 19), "ADX");
        Add(Bit(ecx1, 23), "POPCNT");
        Add(Bit(ecxExt, 5), "LZCNT");
        Add(Bit(ecx1, 22), "MOVBE");
        Add(Bit(ecx1, 30), "RDRAND");
        Add(Bit(ebx7, 18), "RDSEED");
        Add(Bit(edxExt, 20), "NX");
        Add(Bit(edx7, 15), "Hybrid");

        cpu.HasSmt = Bit(edx1, 28);

        if (cpu.Vendor == "Intel" && maxLeaf >= 4)
            ReadCaches(cpu.Caches, 4);
        else if (cpu.Vendor == "AMD" && maxExt >= 0x8000001D && Bit(ecxExt, 22))
            ReadCaches(cpu.Caches, 0x8000001D);
    }

    /// <summary>Deterministic cache parameters (Intel leaf 4 / AMD leaf 0x8000001D share a layout).</summary>
    private static void ReadCaches(List<CacheInfo> caches, uint leaf)
    {
        for (uint index = 0; index < 16; index++)
        {
            var (a, b, c, _) = Leaf(leaf, index);
            int type = (int)(a & 0x1F);
            if (type == 0) break;

            int level = (int)((a >> 5) & 0x7);
            int sharedBy = (int)((a >> 14) & 0xFFF) + 1;
            int ways = (int)((b >> 22) & 0x3FF) + 1;
            int partitions = (int)((b >> 12) & 0x3FF) + 1;
            int lineSize = (int)(b & 0xFFF) + 1;
            long sets = (long)c + 1;
            long sizeKb = ways * partitions * lineSize * sets / 1024;
            string typeName = type switch { 1 => "Data", 2 => "Instruction", 3 => "Unified", _ => "Other" };
            caches.Add(new CacheInfo(level, typeName, sizeKb, ways, lineSize, sharedBy));
        }
    }

    private static string Ascii(params uint[] registers)
    {
        var bytes = new List<byte>(registers.Length * 4);
        foreach (var r in registers)
        {
            bytes.Add((byte)r);
            bytes.Add((byte)(r >> 8));
            bytes.Add((byte)(r >> 16));
            bytes.Add((byte)(r >> 24));
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
