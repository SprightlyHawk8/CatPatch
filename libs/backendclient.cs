using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CatPatchspace
{
    internal static class GarythecatBackendClient
    {
        private const string BackendBaseUrl = "https://backend.cat-bot.de";

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10.0)
        };

        internal static bool UploadSpray(
            byte[] imageBytes,
            out GarythecatSprayUploadResponse response,
            out string error)
        {
            GarythecatSprayUploadRequest payload = new GarythecatSprayUploadRequest
            {
                png_base64 = Convert.ToBase64String(imageBytes ?? Array.Empty<byte>())
            };

            return TryRequest(
                HttpMethod.Post,
                "/sprays/upload",
                payload,
                out response,
                out error);
        }

        internal static bool DownloadSpray(
            string sprayId,
            out byte[] imageBytes,
            out string error)
        {
            string cleanSprayId = (sprayId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(cleanSprayId))
            {
                imageBytes = null;
                error = "spray_id_missing";
                return false;
            }

            return TryRequestBytes(
                HttpMethod.Get,
                "/sprays/" + Uri.EscapeDataString(cleanSprayId),
                out imageBytes,
                out error);
        }

        private static bool TryRequest<T>(
            HttpMethod method,
            string path,
            object payload,
            out T response,
            out string error)
        {
            response = default(T);
            error = null;

            try
            {
                using (HttpRequestMessage request = new HttpRequestMessage(method, BuildUrl(path)))
                {
                    if (payload != null)
                    {
                        string json = JsonConvert.SerializeObject(payload);
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    }

                    using (HttpResponseMessage result = Http.SendAsync(request).GetAwaiter().GetResult())
                    {
                        string body = result.Content == null
                            ? ""
                            : result.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                        if (!result.IsSuccessStatusCode)
                        {
                            error = ExtractError(body, (int)result.StatusCode);
                            return false;
                        }

                        if (string.IsNullOrWhiteSpace(body))
                            return true;

                        T parsed = JsonConvert.DeserializeObject<T>(body);
                        if (parsed == null)
                        {
                            error = "empty_response";
                            return false;
                        }

                        response = parsed;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                error = IsConnectivityFailure(ex)
                    ? "backend_unreachable: " + ex.Message
                    : ex.Message;

                return false;
            }
        }

        private static bool TryRequestBytes(
            HttpMethod method,
            string path,
            out byte[] response,
            out string error)
        {
            response = null;
            error = null;

            try
            {
                using (HttpRequestMessage request = new HttpRequestMessage(method, BuildUrl(path)))
                {
                    using (HttpResponseMessage result = Http.SendAsync(request).GetAwaiter().GetResult())
                    {
                        byte[] bytes = result.Content == null
                            ? Array.Empty<byte>()
                            : result.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

                        if (!result.IsSuccessStatusCode)
                        {
                            string body = Encoding.UTF8.GetString(bytes ?? Array.Empty<byte>());
                            error = ExtractError(body, (int)result.StatusCode);
                            return false;
                        }

                        response = bytes ?? Array.Empty<byte>();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                error = IsConnectivityFailure(ex)
                    ? "backend_unreachable: " + ex.Message
                    : ex.Message;

                return false;
            }
        }

        private static string BuildUrl(string path)
        {
            string cleanBaseUrl = BackendBaseUrl.TrimEnd('/');

            if (string.IsNullOrWhiteSpace(path))
                return cleanBaseUrl;

            if (!path.StartsWith("/", StringComparison.Ordinal))
                path = "/" + path;

            return cleanBaseUrl + path;
        }

        private static bool IsConnectivityFailure(Exception ex)
        {
            return ex != null
                && (ex is TimeoutException
                    || ex is HttpRequestException
                    || ex is SocketException
                    || ex is IOException
                    || ex is TaskCanceledException
                    || IsConnectivityFailure(ex.InnerException));
        }

        private static string ExtractError(string body, int statusCode)
        {
            try
            {
                GarythecatBackendError backendError =
                    JsonConvert.DeserializeObject<GarythecatBackendError>(body);

                if (backendError != null)
                {
                    string message = string.IsNullOrWhiteSpace(backendError.details)
                        ? backendError.error
                        : backendError.details;

                    if (!string.IsNullOrWhiteSpace(message))
                        return message.Trim();
                }
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(body))
                return body.Trim();

            return "HTTP " + statusCode;
        }
    }
}
