using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;

namespace CatPatchspace
{
    internal static class GarythecatBackendClient
    {
        private const string BackendBaseUrl = "https://backend.cat-bot.de";
        private static int _nextRequestId;

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

            bool success = TryRequest(
                HttpMethod.Post,
                "/sprays/upload",
                payload,
                out response,
                out error);
            if (success && (response == null || string.IsNullOrWhiteSpace(response.spray_id)))
            {
                error = "upload_response: spray_id_missing";
                Plugin.Logger.LogWarning("[Sprays] stage=upload.response failed: " + error);
                return false;
            }
            return success;
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
            int requestId = Interlocked.Increment(ref _nextRequestId);
            Stopwatch timer = Stopwatch.StartNew();
            string stage = "serialize";

            try
            {
                using (HttpRequestMessage request = new HttpRequestMessage(method, BuildUrl(path)))
                {
                    if (payload != null)
                    {
                        string json = JsonConvert.SerializeObject(payload);
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    }

                    stage = "http.send";
                    LogRequest(requestId, stage, method, path, timer, "started timeout_s=10");
                    using (HttpResponseMessage result = Http.SendAsync(request).GetAwaiter().GetResult())
                    {
                        stage = "http.read";
                        LogRequest(requestId, stage, method, path, timer, "status=" + (int)result.StatusCode);
                        string body = result.Content == null
                            ? ""
                            : result.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                        if (!result.IsSuccessStatusCode)
                        {
                            error = "HTTP " + (int)result.StatusCode + ": " + ExtractError(body, (int)result.StatusCode);
                            LogRequest(requestId, stage, method, path, timer, "failed " + error, true);
                            return false;
                        }

                        if (string.IsNullOrWhiteSpace(body))
                            throw new InvalidDataException("empty_response");

                        stage = "json.decode";
                        T parsed = JsonConvert.DeserializeObject<T>(body);
                        if (parsed == null)
                        {
                            error = "empty_response";
                            LogRequest(requestId, stage, method, path, timer, "failed " + error, true);
                            return false;
                        }

                        response = parsed;
                        LogRequest(requestId, "complete", method, path, timer, "ok");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                error = IsConnectivityFailure(ex)
                    ? "backend_unreachable: " + ex.Message
                    : ex.Message;
                LogRequest(requestId, stage, method, path, timer, DescribeException(ex), true);
                error = stage + ": " + error;

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
            int requestId = Interlocked.Increment(ref _nextRequestId);
            Stopwatch timer = Stopwatch.StartNew();
            string stage = "http.send";

            try
            {
                using (HttpRequestMessage request = new HttpRequestMessage(method, BuildUrl(path)))
                {
                    LogRequest(requestId, stage, method, path, timer, "started timeout_s=10");
                    using (HttpResponseMessage result = Http.SendAsync(request).GetAwaiter().GetResult())
                    {
                        stage = "http.read";
                        LogRequest(requestId, stage, method, path, timer, "status=" + (int)result.StatusCode);
                        byte[] bytes = result.Content == null
                            ? Array.Empty<byte>()
                            : result.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

                        if (!result.IsSuccessStatusCode)
                        {
                            string body = Encoding.UTF8.GetString(bytes ?? Array.Empty<byte>());
                            error = "HTTP " + (int)result.StatusCode + ": " + ExtractError(body, (int)result.StatusCode);
                            LogRequest(requestId, stage, method, path, timer, "failed " + error, true);
                            return false;
                        }

                        if (bytes == null || bytes.Length == 0)
                            throw new InvalidDataException("empty_image_response");
                        response = bytes;
                        LogRequest(requestId, "complete", method, path, timer,
                            "ok bytes=" + bytes.Length + " content_type=" + result.Content?.Headers.ContentType);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                error = IsConnectivityFailure(ex)
                    ? "backend_unreachable: " + ex.Message
                    : ex.Message;
                LogRequest(requestId, stage, method, path, timer, DescribeException(ex), true);
                error = stage + ": " + error;

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

        private static void LogRequest(int id, string stage, HttpMethod method, string path,
            Stopwatch timer, string detail, bool failed = false)
        {
            string line = "[Sprays] request=" + id + " stage=" + stage + " method=" + method
                + " url=" + BuildUrl(path) + " elapsed_ms=" + timer.ElapsedMilliseconds
                + " " + Sanitize(detail);
            if (failed) Plugin.Logger.LogWarning(line);
            else Plugin.Logger.LogInfo(line);
        }

        private static string DescribeException(Exception exception)
        {
            StringBuilder result = new StringBuilder("failed ");
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                result.Append(current.GetType().Name).Append(": ").Append(current.Message);
                if (current is SocketException socket)
                    result.Append(" socket_error=").Append(socket.SocketErrorCode);
                if (current.InnerException != null) result.Append(" -> ");
            }
            return result.ToString();
        }

        private static string Sanitize(string value)
        {
            string clean = (value ?? "").Replace("\0", "").Replace('\r', ' ').Replace('\n', ' ');
            return clean.Length <= 800 ? clean : clean.Substring(0, 800);
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
