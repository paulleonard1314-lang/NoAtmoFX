// Menu NoAtmoFX > Build bundle: builds the 3D noise textures, the material and the "noatmofx" asset bundle.
// Output: <project>/NoAtmoFX_Build/noatmofx  -> copy it to BepInEx/plugins/NoAtmoFX/
// If the game is installed in the default Steam folder the bundle is copied there too.
using System.IO;
using UnityEditor;
using UnityEngine;

public static class NoAtmoFXBuild
{
    const string Dir = "Assets/NoAtmoFX";

    static Texture3D MakeTex(string name, int size)
    {
        string assetPath = Dir + "/" + name + ".asset";
        var bytes = File.ReadAllBytes(Dir + "/" + name + ".bytes");
        var tex = new Texture3D(size, size, size, TextureFormat.RGBA32, true);
        tex.name = name;
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        var cols = new Color32[size * size * size];
        for (int i = 0; i < cols.Length; i++)
            cols[i] = new Color32(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], bytes[i * 4 + 3]);
        tex.SetPixels32(cols);
        tex.Apply(true, false);
        AssetDatabase.DeleteAsset(assetPath);
        AssetDatabase.CreateAsset(tex, assetPath);
        return AssetDatabase.LoadAssetAtPath<Texture3D>(assetPath);
    }

    static Texture2D MakeTex2D(string name, int size)
    {
        string assetPath = Dir + "/" + name + ".asset";
        var bytes = File.ReadAllBytes(Dir + "/" + name + ".bytes");
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true);
        tex.name = name;
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 8;
        var cols = new Color32[size * size];
        for (int i = 0; i < cols.Length; i++)
            cols[i] = new Color32(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], bytes[i * 4 + 3]);
        tex.SetPixels32(cols);
        tex.Apply(true, false);
        AssetDatabase.DeleteAsset(assetPath);
        AssetDatabase.CreateAsset(tex, assetPath);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    [MenuItem("NoAtmoFX/Build bundle")]
    public static void Build()
    {
        var shape = MakeTex("NOClouds_Shape", 128);
        var detail = MakeTex("NOClouds_Detail", 32);
        var cirrus = MakeTex2D("NOClouds_Cirrus", 1024);
        AssetDatabase.ImportAsset(Dir + "/NOClouds.shader", ImportAssetOptions.ForceUpdate);
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(Dir + "/NOClouds.shader");
        if (shader == null) { Debug.LogError("NoAtmoFX: shader not found"); return; }
        foreach (var m in ShaderUtil.GetShaderMessages(shader))
            Debug.Log("NoAtmoFX shader: " + m.severity + " " + m.message + " line " + m.line);
        if (ShaderUtil.ShaderHasError(shader)) { Debug.LogError("NoAtmoFX: shader has errors, bundle NOT built"); return; }
        foreach (var extra in new[] { "NOCWetGlass", "NOCWetBrush", "NOCRain" })
        {
            AssetDatabase.ImportAsset(Dir + "/" + extra + ".shader", ImportAssetOptions.ForceUpdate);
            var sh = AssetDatabase.LoadAssetAtPath<Shader>(Dir + "/" + extra + ".shader");
            if (sh == null) { Debug.LogError("NoAtmoFX: " + extra + ".shader not found"); return; }
            foreach (var m in ShaderUtil.GetShaderMessages(sh))
                Debug.Log("NoAtmoFX " + extra + ": " + m.severity + " " + m.message + " line " + m.line);
            if (ShaderUtil.ShaderHasError(sh)) { Debug.LogError("NoAtmoFX: " + extra + " has errors, bundle NOT built"); return; }
        }

        string matPath = Dir + "/NOClouds.mat";
        AssetDatabase.DeleteAsset(matPath);
        var mat = new Material(shader) { name = "NOClouds" };
        mat.SetTexture("_ShapeTex", shape);
        mat.SetTexture("_DetailTex", detail);
        mat.SetTexture("_CirrusTex", cirrus);
        AssetDatabase.CreateAsset(mat, matPath);
        AssetDatabase.SaveAssets();

        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../NoAtmoFX_Build"));
        Directory.CreateDirectory(outDir);
        var b = new AssetBundleBuild
        {
            assetBundleName = "noatmofx",
            assetNames = new[] { matPath, Dir + "/NOClouds.shader", Dir + "/NOClouds_Shape.asset", Dir + "/NOClouds_Detail.asset", Dir + "/NOClouds_Cirrus.asset", Dir + "/NOCWetGlass.shader", Dir + "/NOCWetBrush.shader", Dir + "/NOCRain.shader" }
        };
        BuildPipeline.BuildAssetBundles(outDir, new[] { b }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        Debug.Log("NoAtmoFX: bundle built at " + outDir + "\\noatmofx");
        const string game = @"C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option\BepInEx\plugins\NoAtmoFX";
        try
        {
            if (Directory.Exists(game))
            {
                File.Copy(Path.Combine(outDir, "noatmofx"), Path.Combine(game, "noatmofx"), true);
                Debug.Log("NoAtmoFX: bundle copied to " + game);
            }
        }
        catch (System.Exception e) { Debug.LogWarning("NoAtmoFX: could not copy to the game folder (" + e.Message + "). Copy it by hand."); }
        EditorUtility.RevealInFinder(Path.Combine(outDir, "noatmofx"));
    }
}
