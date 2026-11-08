using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using CatPatchspace;
using UnityEngine;

// Optional local test plugin; never included in CatPatch.dll. Runs on Unity's
// main thread. Install only for a test launch and remove afterward.
[BepInPlugin("meow.catpatch.spraychecks", "CatPatch Spray Checks", "1.0.0")]
[BepInDependency("meow.catpatch")]
public sealed class SprayRuntimeChecks : BaseUnityPlugin
{
    private int _passed;
    private int _failed;
    private IEnumerator Start()
    {
        yield return new WaitForSecondsRealtime(3f);
        try { Run(); }
        catch (Exception ex) { _failed++; Logger.LogError(ex.ToString()); }
        Logger.LogInfo("SPRAY_CHECKS_COMPLETE passed=" + _passed + " failed=" + _failed);
    }

    private void Check(bool condition, string name)
    {
        if (!condition) { _failed++; Logger.LogError("FAIL " + name); }
        else { _passed++; Logger.LogInfo("PASS " + name); }
    }

    private void Run()
    {
        string directory = Environment.GetEnvironmentVariable("CATPATCH_SPRAY_CHECK_DIR");
        if (string.IsNullOrEmpty(directory)) throw new Exception("Set CATPATCH_SPRAY_CHECK_DIR to GIF fixtures.");
        Assembly patch = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "CatPatch");
        Type visualType = patch.GetType("CatPatchspace.SprayVisualController", true);
        object visuals = Activator.CreateInstance(visualType, true);
        Type controllerType = patch.GetType("CatPatchspace.SprayController", true);
        var plugin = BepInEx.Bootstrap.Chainloader.PluginInfos["meow.catpatch"].Instance;
        object controller = plugin.GetComponent(controllerType);
        Type inspectorType = patch.GetType("CatPatchspace.SprayInspector", true);
        object inspector = plugin.GetComponent(inspectorType);
        foreach (string path in Directory.GetFiles(directory, "*.gif"))
        {
            string name = Path.GetFileName(path);
            byte[] bytes = File.ReadAllBytes(path);
            Texture2D first;
            string error;
            bool isGif = GifAnimationDecoder.IsGifBytes(bytes);
            if (isGif) Check(GifAnimationDecoder.TryCreateFirstFrameTexture(bytes, out first, out error), name + " first frame: " + error);
            else
            {
                first = new Texture2D(2,2); error = "";
                Check(SprayImageSafety.TryValidate(bytes, out _, out error) && ImageConversion.LoadImage(first, bytes), name + " static frame: " + error);
            }
            if (first == null) continue;
            Check(first.GetPixels32().Any(c => c.a > 0), name + " first frame contains visible pixels");
            object[] previewArgs = { path, null };
            Check((bool)controllerType.GetMethod("TryGetPreviewTexture", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, previewArgs), name + " selector loads");
            Check(((Texture2D)previewArgs[1]).GetPixels32().Any(c => c.a > 0), name + " selector visible");
            object[] visualArgs = { name, bytes, Vector2.zero, 0UL, null, null };
            Check((bool)visualType.GetMethod("TryCreateSpray").Invoke(visuals, visualArgs), name + " wall sprite: " + visualArgs[5]);
            GameObject wall = visualArgs[4] as GameObject;
            if (wall != null)
            {
                Check(wall.GetComponent<SpriteRenderer>().sprite.texture.GetPixels32().Any(c => c.a > 0), name + " wall sprite visible");
                object instance = wall.GetComponent(patch.GetType("CatPatchspace.SprayInstance"));
                object[] inspectArgs = { instance, null };
                bool inspected = (bool)inspectorType.GetMethod("TryGetGifAnimation", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(inspector, inspectArgs);
                Check(inspected == GifAnimationDecoder.IsGifBytes(bytes), name + " inspector selects animation only for GIF bytes");
                instance.GetType().GetProperty("SourceFilePath").SetValue(instance, path);
                Check((bool)inspectorType.GetMethod("TryGetGifAnimation", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(inspector, inspectArgs) == GifAnimationDecoder.IsGifBytes(bytes), name + " local inspector uses actual bytes despite extension");
                UnityEngine.Object.Destroy(wall);
            }
            GifAnimationData animation;
            Check(GifAnimationDecoder.TryLoad(bytes, out animation, out error) == isGif, name + " GIF-only animation decoder: " + error);
            if (animation != null)
            {
                Check(animation.Frames.Length > 0 && animation.TotalDuration > 0, name + " valid timing");
                if (animation.Frames.Length > 1)
                {
                    string[] hashes = animation.Frames.Select(f => Convert.ToBase64String(ImageConversion.EncodeToPNG(f))).ToArray();
                    Check(hashes.Distinct().Count() > 1, name + " animated frames differ");
                    Check(animation.GetFrame(animation.TotalDuration * 2.0 + 0.001) == animation.Frames[0], name + " loops");
                    if (name == "partial-frames.gif")
                    {
                        Color32[] firstPixels = animation.Frames[0].GetPixels32();
                        Color32[] nextPixels = animation.Frames[1].GetPixels32();
                        Check(firstPixels[0].r > 200 && firstPixels[1].g > 200 && nextPixels.All(p => p.g > 200),
                            "GIF subframes preserve unchanged canvas pixels");
                    }
                }
                animation.DestroyTextures();
            }
            UnityEngine.Object.Destroy(first);
        }
        GifAnimationData invalid;
        string invalidError;
        Check(!GifAnimationDecoder.TryLoad(new byte[] { 1, 2, 3 }, out invalid, out invalidError) && invalid == null && invalidError.Length > 0,
            "corrupt GIF fails with reason");
        byte[] enormous = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        enormous[6] = enormous[8] = 0xff; enormous[7] = enormous[9] = 0x7f;
        Check(!GifAnimationDecoder.TryLoad(enormous, out invalid, out invalidError) && invalid == null,
            "enormous GIF rejected by preflight without native allocation");
        object[] rejectedVisual = { "unsafe", enormous, Vector2.zero, 0UL, null, null };
        Check(!(bool)visualType.GetMethod("TryCreateSpray").Invoke(visuals, rejectedVisual), "shipping wall renderer rejects enormous image");
        visualType.GetMethod("ClearActiveSprays").Invoke(visuals, null);
        byte[] validFixture = File.ReadAllBytes(Path.Combine(directory, "indexed.gif"));
        object[] boundedVisual = { "budget-test", validFixture, Vector2.zero, 0UL, null, null };
        visualType.GetField("_spritePixelBytes", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(visuals, 128L * 1024 * 1024);
        Check(!(bool)visualType.GetMethod("TryCreateSpray").Invoke(visuals, boundedVisual), "shipping renderer rejects extra texture memory at its budget");
        visualType.GetMethod("ClearActiveSprays").Invoke(visuals, null);
        visualType.GetField("_spriteEncodedBytes", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(visuals, 64L * 1024 * 1024);
        Check(!(bool)visualType.GetMethod("TryCreateSpray").Invoke(visuals, boundedVisual), "shipping renderer rejects extra encoded cache bytes at its budget");
        visualType.GetMethod("ClearActiveSprays").Invoke(visuals, null);
        var spriteCache = (System.Collections.IDictionary)visualType.GetField("_spriteCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(visuals);
        for (int i = 0; i < 128; i++) spriteCache.Add("entry-" + i, null);
        Check(!(bool)visualType.GetMethod("TryCreateSpray").Invoke(visuals, boundedVisual), "shipping renderer enforces texture-count budget");
        visualType.GetMethod("ClearActiveSprays").Invoke(visuals, null);
        var gifCache = (System.Collections.IDictionary)inspectorType.GetField("_gifAnimationCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(inspector);
        Check(gifCache.Count <= 1, "shipping inspector retains at most one decoded animation");
        object network = controllerType.GetField("_networkController", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
        MethodInfo accept = network.GetType().GetMethod("AcceptPlacement", BindingFlags.Instance | BindingFlags.NonPublic);
        bool firstFour = true;
        for (int i = 0; i < 4; i++) firstFour &= (bool)accept.Invoke(network, new object[] { (ushort)65000 });
        Check(firstFour && !(bool)accept.Invoke(network, new object[] { (ushort)65000 }), "shipping placement guard bounds repeated immediate peer requests");
        Check((bool)accept.Invoke(network, new object[] { (ushort)65001 }), "another peer retains placement allowance after a peer exhausts its quota");
        MethodInfo parse = network.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "TryReadSprayPacket" && m.GetParameters().Length == 4);
        foreach (float x in new[] { float.NaN, float.PositiveInfinity, 1e12f, 12f })
        {
            var writer = new LiteNetLib.Utils.NetDataWriter(); writer.Put("abcdef"); writer.Put(x); writer.Put(0f); writer.Put(0UL);
            object[] args = { new LiteNetLib.Utils.NetDataReader(writer.CopyData()), "", Vector2.zero, 0UL };
            Check((bool)parse.Invoke(null, args) == (x == 12f), "shipping packet parser bounds position " + x);
        }
        object lobbyBudget = network.GetType().GetField("_placementGlobal", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(network);
        FieldInfo tokens = lobbyBudget.GetType().GetField("_tokens", BindingFlags.Instance | BindingFlags.NonPublic);
        double tokensBefore = (double)tokens.GetValue(lobbyBudget);
        MethodInfo receive = network.GetType().GetMethod("OnClientSprayReceived", BindingFlags.Static | BindingFlags.NonPublic);
        for (int i = 0; i < 8; i++)
        {
            var writer = new LiteNetLib.Utils.NetDataWriter(); writer.Put("../invalid");
            object[] args = { Activator.CreateInstance(receive.GetParameters()[0].ParameterType), new LiteNetLib.Utils.NetDataReader(writer.CopyData()) };
            receive.Invoke(null, args); // Invalid ID exits before downloads or Unity object creation.
        }
        Check((double)tokens.GetValue(lobbyBudget) < tokensBefore - 7,
            "client applies lobby allowance rather than one player's four-event burst to host broadcasts");
        var a = new Texture2D(1, 1);
        var b = new Texture2D(1, 1);
        var timing = new GifAnimationData(new[] { a, b }, new[] { 0.02f, 0.03f });
        Check(Mathf.Abs(timing.TotalDuration - 0.05f) < 0.001f && timing.GetFrame(0.025) == b, "fast frame timing preserved");
        timing.DestroyTextures();
        CheckLayout(controllerType);
        BackendChecks.Run(Check, line => Logger.LogInfo(line));
    }

    private void CheckLayout(Type controller)
    {
        MethodInfo scaleMethod = controller.GetMethod("CalculateRadialScale", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo centerMethod = controller.GetMethod("CalculateRadialCenter", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo rectMethod = controller.GetMethod("GetRadialRect", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var size in new[] { new Vector2Int(800, 600), new Vector2Int(1280, 720), new Vector2Int(1920, 1080), new Vector2Int(3840, 2160), new Vector2Int(5120, 1440) })
        foreach (float dpi in new[] { 0f, 96f, 192f })
        foreach (float preference in new[] { 0.75f, 1f, 3f })
        {
            float scale = (float)scaleMethod.Invoke(null, new object[] { size.x, size.y, dpi, preference });
            foreach (Vector2 anchor in new[] { Vector2.zero, new Vector2(size.x / scale, size.y / scale) })
            {
                Vector2 center = (Vector2)centerMethod.Invoke(null, new object[] { anchor, size.x, size.y, scale });
                bool valid = true;
                for (int count = 1; count <= 5; count++)
                for (int index = 0; index < count; index++)
                {
                    Rect rect = (Rect)rectMethod.Invoke(null, new object[] { center, index, count });
                    valid &= rect.xMin >= -0.01f && rect.yMin >= -0.01f && rect.xMax * scale <= size.x + 0.01f && rect.yMax * scale <= size.y + 0.01f;
                    // Hit testing uses the same logical coordinates as drawing.
                    valid &= rect.Contains(rect.center * scale / scale);
                }
                Check(valid, "selector fits " + size + " dpi=" + dpi + " preference=" + preference + " anchor=" + anchor);
            }
        }
        float hd = (float)scaleMethod.Invoke(null, new object[] { 1920, 1080, 96f, 1f });
        float uhd = (float)scaleMethod.Invoke(null, new object[] { 3840, 2160, 96f, 1f });
        float dense = (float)scaleMethod.Invoke(null, new object[] { 3840, 2160, 192f, 1f });
        Check(hd == uhd && dense == uhd * 2f, "selector logical size independent of resolution and scales with DPI");
    }
}
