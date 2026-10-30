using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CatPatchspace;

namespace CatPatchspace
{
    // Only the linked test copy of backendclient uses this logger and fake HTTP.
    internal static class Plugin { internal static TestLog Logger = new TestLog(); }
    internal sealed class TestLog
    {
        internal readonly List<string> Lines = new List<string>();
        internal Action<string> Forward;
        public void LogInfo(string line) { Lines.Add(line); Forward?.Invoke(line); }
        public void LogWarning(string line) { Lines.Add(line); Forward?.Invoke(line); }
    }
}

internal static class BackendChecks
{
    private sealed class FakeHttp : HttpMessageHandler
    {
        internal Func<HttpResponseMessage> Respond;
        internal string Body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            try { return Task.FromResult(Respond()); }
            catch (Exception ex) { return Task.FromException<HttpResponseMessage>(ex); }
        }
    }

    internal static void Run(Action<bool, string> check, Action<string> log)
    {
        Plugin.Logger.Forward = log;
        var fake = new FakeHttp();
        using (var http = new HttpClient(fake))
        {
            FieldInfo field = typeof(GarythecatBackendClient).GetField("Http", BindingFlags.Static | BindingFlags.NonPublic);
            ((HttpClient)field.GetValue(null)).Dispose();
            field.SetValue(null, http);
            GarythecatSprayUploadResponse result;
            string error;
            byte[] bytes;
            byte[] input = { 71, 73, 70, 56, 57, 97 };
            fake.Respond = () => Reply(HttpStatusCode.OK, "{\"spray_id\":\"abcdef\"}");
            check(GarythecatBackendClient.UploadSpray(input, out result, out error) && result.spray_id == "abcdef"
                && fake.Body.Contains(Convert.ToBase64String(input)), "backend upload preserves GIF bytes in existing contract");
            fake.Respond = () => Reply(HttpStatusCode.BadRequest, "{\"error\":\"invalid_image\"}");
            check(!GarythecatBackendClient.UploadSpray(input, out result, out error) && error.Contains("HTTP 400") && error.Contains("invalid_image"), "backend rejection keeps HTTP status and reason");
            foreach (string body in new[] { "", "null", "{}", "not json" })
            {
                fake.Respond = () => Reply(HttpStatusCode.OK, body);
                check(!GarythecatBackendClient.UploadSpray(input, out result, out error) && !string.IsNullOrEmpty(error), "backend invalid success body rejected: " + body);
            }
            fake.Respond = () => Reply(HttpStatusCode.OK, "GIF89a");
            check(GarythecatBackendClient.DownloadSpray("abcdef", out bytes, out error) && bytes.Length == 6, "backend download returns original bytes");
            fake.Respond = () => Reply(HttpStatusCode.OK, "");
            check(!GarythecatBackendClient.DownloadSpray("abcdef", out bytes, out error) && error.Contains("empty_image_response"), "backend empty download rejected");
            fake.Respond = () => Reply(HttpStatusCode.NotFound, "{\"error\":\"not_found\"}");
            check(!GarythecatBackendClient.DownloadSpray("abcdef", out bytes, out error) && error.Contains("HTTP 404"), "backend download HTTP failure");
            fake.Respond = () => throw new HttpRequestException("connection failed", new SocketException((int)SocketError.HostNotFound));
            check(!GarythecatBackendClient.DownloadSpray("abcdef", out bytes, out error) && error.Contains("backend_unreachable"), "backend connectivity failure classified");
            check(Plugin.Logger.Lines.Exists(s => s.Contains("stage=http.send") && s.Contains("socket_error=HostNotFound") && s.Contains("request=") && s.Contains("elapsed_ms=")), "backend logs stage, correlation, duration and inner DNS error");
            fake.Respond = () => throw new TaskCanceledException("timed out");
            check(!GarythecatBackendClient.UploadSpray(input, out result, out error) && error.Contains("backend_unreachable"), "backend timeout classified");
            check(!Plugin.Logger.Lines.Exists(s => s.Contains("png_base64") || s.Contains(Convert.ToBase64String(input))), "backend logs omit image payload");
            check(!Plugin.Logger.Lines.Exists(s => s.Contains("\0") || s.Contains("\r") || s.Contains("\n")), "backend diagnostic lines strip control characters");
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}
