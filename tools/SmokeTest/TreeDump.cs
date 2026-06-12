// 實驗工具：印出 STEP 匯入後的物件階層（HierarchyToBlocks=true）
using CADability;
using CADability.GeoObject;

namespace SmokeTest;

internal static class TreeDump
{
    public static void Run(string path)
    {
        var imp = new ImportStep { HierarchyToBlocks = true };
        GeoObjectList list = imp.Read(path);
        Console.WriteLine($"top-level objects: {list?.Count}");
        if (list is null) return;
        foreach (IGeoObject go in list) Dump(go, 0);
    }

    private static void Dump(IGeoObject go, int indent)
    {
        string name = go switch
        {
            Block b => b.Name,
            Solid s => s.NameOrEmpty,
            Shell sh => sh.NameOrEmpty,
            _ => "",
        };
        Console.WriteLine($"{new string(' ', indent * 2)}{go.GetType().Name} '{name}' children={go.NumChildren}");
        if (indent > 6) return;
        for (int i = 0; i < go.NumChildren; i++)
            if (go.Child(i) is IGeoObject c) Dump(c, indent + 1);
    }
}
