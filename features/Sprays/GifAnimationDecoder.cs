using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace CatPatchspace
{
    internal static class GifAnimationDecoder
    {
        public const float FallbackFrameDurationSeconds = 0.1f;

        private const int GifFrameDelayPropertyId = 0x5100;
        private const int DelayBytesPerFrame = 4;

        private static readonly Type ImageType = ResolveDrawingType("System.Drawing.Image");
        private static readonly Type BitmapType = ResolveDrawingType("System.Drawing.Bitmap");
        private static readonly Type GraphicsType = ResolveDrawingType("System.Drawing.Graphics");
        private static readonly Type ColorType = ResolveDrawingType("System.Drawing.Color");
        private static readonly Type FrameDimensionType = ResolveDrawingType("System.Drawing.Imaging.FrameDimension");
        private static readonly Type ImageFormatType = ResolveDrawingType("System.Drawing.Imaging.ImageFormat");

        public static bool TryLoadFromFile(string path, out GifAnimationData animation, out string error)
        {
            animation = null;
            error = "";

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "GIF source file was not found.";
                return false;
            }

            try
            {
                return TryLoad(File.ReadAllBytes(path), out animation, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryLoad(byte[] gifBytes, out GifAnimationData animation, out string error)
        {
            animation = null;
            error = "";

            if (gifBytes == null || gifBytes.Length == 0)
            {
                error = "GIF file was empty.";
                return false;
            }

            if (!DrawingTypesAvailable())
            {
                error = "The game's System.Drawing GIF decoder is unavailable.";
                return false;
            }

            try
            {
                using (MemoryStream input = new MemoryStream(gifBytes, false))
                using (IDisposable image = LoadImage(input))
                {
                    object dimension = CreateFrameDimension(image);
                    int frameCount = InvokeInt(image, "GetFrameCount", dimension);
                    if (frameCount <= 0)
                    {
                        error = "GIF contained no frames.";
                        return false;
                    }

                    Texture2D[] frames = new Texture2D[frameCount];
                    float[] durations = ReadFrameDurations(image, frameCount);

                    for (int i = 0; i < frameCount; i++)
                    {
                        Invoke(image, "SelectActiveFrame", dimension, i);
                        frames[i] = CreateTextureFromFrame(image, i);
                        if (frames[i] != null)
                            continue;

                        DestroyTextures(frames);
                        error = "Failed to create texture for GIF frame " + i + ".";
                        return false;
                    }

                    animation = new GifAnimationData(frames, durations);
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = UnwrapReflectionException(ex).Message;
                return false;
            }
        }

        public static bool TryCreateFirstFrameTexture(byte[] gifBytes, out Texture2D texture, out string error)
        {
            texture = null;
            error = "";

            if (!DrawingTypesAvailable())
            {
                error = "The game's System.Drawing GIF decoder is unavailable.";
                return false;
            }

            try
            {
                using (MemoryStream input = new MemoryStream(gifBytes, false))
                using (IDisposable image = LoadImage(input))
                {
                    object dimension = CreateFrameDimension(image);
                    Invoke(image, "SelectActiveFrame", dimension, 0);
                    texture = CreateTextureFromFrame(image, 0);
                    if (texture != null)
                        return true;

                    error = "Unity failed to decode the first GIF frame.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = UnwrapReflectionException(ex).Message;
                return false;
            }
        }

        public static bool IsGifBytes(byte[] bytes)
        {
            return bytes != null
                && bytes.Length >= 6
                && bytes[0] == 71
                && bytes[1] == 73
                && bytes[2] == 70
                && bytes[3] == 56
                && (bytes[4] == 55 || bytes[4] == 57)
                && bytes[5] == 97;
        }

        private static Texture2D CreateTextureFromFrame(IDisposable image, int frameIndex)
        {
            int width = GetProperty<int>(image, "Width");
            int height = GetProperty<int>(image, "Height");

            using (IDisposable bitmap = (IDisposable)Activator.CreateInstance(BitmapType, width, height))
            using (IDisposable graphics = (IDisposable)InvokeStatic(GraphicsType, "FromImage", image))
            using (MemoryStream output = new MemoryStream())
            {
                object transparent = ColorType.GetProperty("Transparent", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                Invoke(graphics, "Clear", transparent);
                Invoke(graphics, "DrawImage", image, 0, 0, width, height);

                object png = ImageFormatType.GetProperty("Png", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                Invoke(bitmap, "Save", output, png);

                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, output.ToArray()))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                texture.name = "CatPatch GIF Frame " + frameIndex;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                return texture;
            }
        }

        private static float[] ReadFrameDurations(IDisposable image, int frameCount)
        {
            float[] durations = new float[frameCount];
            for (int i = 0; i < durations.Length; i++)
                durations[i] = FallbackFrameDurationSeconds;

            try
            {
                int[] propertyIds = GetProperty<int[]>(image, "PropertyIdList");
                if (!ArrayContains(propertyIds, GifFrameDelayPropertyId))
                    return durations;

                object item = Invoke(image, "GetPropertyItem", GifFrameDelayPropertyId);
                byte[] values = item == null ? null : GetProperty<byte[]>(item, "Value");
                if (values == null)
                    return durations;

                for (int i = 0; i < frameCount; i++)
                {
                    int offset = i * DelayBytesPerFrame;
                    if (offset + DelayBytesPerFrame > values.Length)
                        break;

                    int delayHundredths = BitConverter.ToInt32(values, offset);
                    if (delayHundredths > 1)
                        durations[i] = delayHundredths / 100f;
                }
            }
            catch
            {
            }

            return durations;
        }

        private static IDisposable LoadImage(Stream stream)
        {
            MethodInfo method = ImageType.GetMethod(
                "FromStream",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Stream), typeof(bool), typeof(bool) },
                null);
            return (IDisposable)method.Invoke(null, new object[] { stream, true, true });
        }

        private static object CreateFrameDimension(IDisposable image)
        {
            Guid[] dimensions = GetProperty<Guid[]>(image, "FrameDimensionsList");
            if (dimensions == null || dimensions.Length == 0)
                throw new InvalidOperationException("GIF has no frame dimensions.");

            return Activator.CreateInstance(FrameDimensionType, dimensions[0]);
        }

        private static object Invoke(object target, string methodName, params object[] args)
        {
            MethodInfo method = FindCompatibleMethod(target.GetType(), methodName, args, false);
            return method.Invoke(target, args);
        }

        private static object InvokeStatic(Type type, string methodName, params object[] args)
        {
            MethodInfo method = FindCompatibleMethod(type, methodName, args, true);
            return method.Invoke(null, args);
        }

        private static int InvokeInt(object target, string methodName, params object[] args)
        {
            return Convert.ToInt32(Invoke(target, methodName, args));
        }

        private static MethodInfo FindCompatibleMethod(Type type, string methodName, object[] args, bool isStatic)
        {
            BindingFlags flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            MethodInfo[] methods = type.GetMethods(flags);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != methodName)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != args.Length)
                    continue;

                bool compatible = true;
                for (int j = 0; j < parameters.Length; j++)
                {
                    if (args[j] != null && !parameters[j].ParameterType.IsInstanceOfType(args[j]))
                    {
                        compatible = false;
                        break;
                    }
                }

                if (compatible)
                    return method;
            }

            throw new MissingMethodException(type.FullName, methodName);
        }

        private static T GetProperty<T>(object target, string propertyName)
        {
            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
                throw new MissingMemberException(target.GetType().FullName, propertyName);

            return (T)property.GetValue(target, null);
        }

        private static Type ResolveDrawingType(string fullName)
        {
            return Type.GetType(fullName + ", System.Drawing", false);
        }

        private static bool DrawingTypesAvailable()
        {
            return ImageType != null
                && BitmapType != null
                && GraphicsType != null
                && ColorType != null
                && FrameDimensionType != null
                && ImageFormatType != null;
        }

        private static Exception UnwrapReflectionException(Exception exception)
        {
            TargetInvocationException invocation = exception as TargetInvocationException;
            return invocation != null && invocation.InnerException != null
                ? invocation.InnerException
                : exception;
        }

        private static bool ArrayContains(int[] values, int expected)
        {
            if (values == null)
                return false;

            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == expected)
                    return true;
            }

            return false;
        }

        private static void DestroyTextures(Texture2D[] textures)
        {
            if (textures == null)
                return;

            for (int i = 0; i < textures.Length; i++)
            {
                if (textures[i] != null)
                    UnityEngine.Object.Destroy(textures[i]);
            }
        }
    }
}
