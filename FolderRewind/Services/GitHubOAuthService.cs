using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    internal static class GitHubOAuthService
    {
        private const string OAuthAuthorizeUrl = "https://github.com/login/oauth/authorize";
        private const string OAuthAccessTokenUrl = "https://github.com/login/oauth/access_token";
        private const string GitHubApiBaseUrl = "https://api.github.com";
        private const string OAuthScope = "public_repo";
        private const string BuiltInClientId = "Ov23liwLdVrnGVZmwxFR";
        // 发布 Microsoft Store 版时由维护者临时填入真实 secret。
        // 公开仓库、GitHub 侧载版和任何第三方构建都不应包含 live secret。定时替换client_secret避免出现意外…希望吧。
        private const string BuiltInClientSecret = "__SET_GITHUB_OAUTH_CLIENT_SECRET_FOR_STORE_RELEASE__";
        private const string BuiltInRedirectUri = "http://127.0.0.1:45731/callback/";
        private static readonly HttpClient Http = CreateClient();

        internal static string ClientId => BuiltInClientId;
        internal static string RedirectUri => BuiltInRedirectUri;

        internal sealed class GitHubAuthenticationState
        {
            public bool IsConfigured { get; init; }
            public bool HasToken { get; init; }
            public bool IsAuthenticated { get; init; }
            public string UserLogin { get; init; } = string.Empty;
            public string Message { get; init; } = string.Empty;
        }

        internal sealed class GitHubOAuthResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public string UserLogin { get; init; } = string.Empty;
        }

        private sealed class OAuthCallbackData
        {
            public string Code { get; init; } = string.Empty;
            public string State { get; init; } = string.Empty;
            public string Error { get; init; } = string.Empty;
            public string ErrorDescription { get; init; } = string.Empty;
        }

        public static string GetStoredAccessToken()
        {
            return GitHubSecretStoreService.GetAccessToken();
        }

        public static string GetStoredUserLogin()
        {
            return GitHubSecretStoreService.GetUserLogin();
        }

        public static string GetConfiguredClientSecret()
        {
            return HasUsableClientSecret() ? BuiltInClientSecret : string.Empty;
        }

        public static bool IsConfigured(out string message)
        {
            if (ConfigService.CurrentConfig?.GlobalSettings == null)
            {
                message = I18n.GetString("GitHubOAuth_ConfigUnavailable");
                return false;
            }

            if (!HasUsableClientSecret())
            {
                message = I18n.GetString("GitHubOAuth_ConfigUnavailable");
                return false;
            }

            message = string.Empty;
            return true;
        }

        public static async Task<GitHubAuthenticationState> GetAuthenticationStateAsync(bool validateToken, CancellationToken ct = default)
        {
            var configured = IsConfigured(out var configMessage);
            var token = GetStoredAccessToken();
            var cachedLogin = GetStoredUserLogin();

            if (!configured)
            {
                return new GitHubAuthenticationState
                {
                    IsConfigured = false,
                    HasToken = !string.IsNullOrWhiteSpace(token),
                    UserLogin = cachedLogin,
                    Message = configMessage
                };
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return new GitHubAuthenticationState
                {
                    IsConfigured = true,
                    HasToken = false,
                    Message = I18n.GetString("GitHubOAuth_NotAuthorized")
                };
            }

            if (!validateToken)
            {
                return new GitHubAuthenticationState
                {
                    IsConfigured = true,
                    HasToken = true,
                    IsAuthenticated = true,
                    UserLogin = cachedLogin,
                    Message = string.IsNullOrWhiteSpace(cachedLogin)
                        ? I18n.GetString("GitHubOAuth_Authorized")
                        : I18n.Format("GitHubOAuth_AuthorizedAs", cachedLogin)
                };
            }

            var user = await GetCurrentUserAsync(token, ct);
            if (!user.Success)
            {
                return new GitHubAuthenticationState
                {
                    IsConfigured = true,
                    HasToken = true,
                    IsAuthenticated = false,
                    UserLogin = cachedLogin,
                    Message = string.IsNullOrWhiteSpace(user.Message)
                        ? I18n.GetString("GitHubOAuth_TokenInvalid")
                        : user.Message
                };
            }

            GitHubSecretStoreService.SetUserLogin(user.UserLogin);
            return new GitHubAuthenticationState
            {
                IsConfigured = true,
                HasToken = true,
                IsAuthenticated = true,
                UserLogin = user.UserLogin,
                Message = I18n.Format("GitHubOAuth_AuthorizedAs", user.UserLogin)
            };
        }

        public static async Task<GitHubOAuthResult> SignInAsync(XamlRoot? xamlRoot, CancellationToken ct = default)
        {
            if (!IsConfigured(out var configMessage))
            {
                return new GitHubOAuthResult
                {
                    Success = false,
                    Message = configMessage
                };
            }

            if (!Uri.TryCreate(BuiltInRedirectUri, UriKind.Absolute, out var redirectUri))
            {
                return new GitHubOAuthResult
                {
                    Success = false,
                    Message = I18n.GetString("GitHubOAuth_ConfigUnavailable")
                };
            }

            var listenerPrefix = BuildListenerPrefix(redirectUri);

            // WinUI 桌面应用属于 public client，静态 secret 不能作为真实身份凭证。
            // 这里仍然使用 PKCE，减少授权码被拦截后被重放利用的风险。
            var state = CreateRandomToken(24);
            var codeVerifier = CreateRandomToken(64);
            var codeChallenge = CreateCodeChallenge(codeVerifier);
            var authorizeUri = BuildAuthorizeUri(BuiltInClientId, redirectUri, state, codeChallenge);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var listener = new HttpListener();

            try
            {
                listener.Prefixes.Add(listenerPrefix);
                // 回调监听只绑定到固定本机地址，配合 GitHub OAuth App 预先配置的回调地址使用。
                listener.Start();
            }
            catch (Exception ex)
            {
                return new GitHubOAuthResult
                {
                    Success = false,
                    Message = I18n.Format("GitHubOAuth_ListenerStartFailedWithReason", ex.Message)
                };
            }

            var callbackTask = WaitForCallbackAsync(listener, cts.Token);
            if (!ShellPathService.TryOpenPath(authorizeUri.ToString(), out var openError))
            {
                cts.Cancel();
                return new GitHubOAuthResult
                {
                    Success = false,
                    Message = I18n.Format("GitHubOAuth_BrowserOpenFailedWithReason", openError ?? string.Empty)
                };
            }

            Task hideTask = Task.CompletedTask;
            if (xamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("GitHubOAuth_LoginTitle"),
                    Content = new TextBlock
                    {
                        Text = I18n.GetString("GitHubOAuth_LoginHint"),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 440
                    },
                    CloseButtonText = I18n.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = xamlRoot
                };

                hideTask = callbackTask.ContinueWith(async task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        await TemplateDialogCoordinatorService.HideAsync(dialog);
                    }
                }, TaskScheduler.Default).Unwrap();

                var dialogResult = await TemplateDialogCoordinatorService.ShowAsync(dialog, xamlRoot, ct);
                if (dialogResult == ContentDialogResult.None && !callbackTask.IsCompleted)
                {
                    cts.Cancel();
                    return new GitHubOAuthResult
                    {
                        Success = false,
                        Message = I18n.GetString("GitHubOAuth_LoginCanceled")
                    };
                }
            }

            OAuthCallbackData callback;
            try
            {
                callback = await callbackTask;
            }
            catch (OperationCanceledException)
            {
                return new GitHubOAuthResult
                {
                    Success = false,
                    Message = I18n.GetString("GitHubOAuth_LoginCanceled")
                };
            }
            finally
            {
                await hideTask;
            }

            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                return new GitHubOAuthResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(callback.ErrorDescription)
                        ? callback.Error
                        : callback.ErrorDescription
                };
            }

            if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            {
                // state 不匹配说明回调不是本次授权发起的，必须立即终止，防止 CSRF/串线。
                return new GitHubOAuthResult
                {
                    Success = false,
                    Message = I18n.GetString("GitHubOAuth_StateMismatch")
                };
            }

            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                return new GitHubOAuthResult
                {
                    Success = false,
                    Message = I18n.GetString("GitHubOAuth_MissingAuthorizationCode")
                };
            }

            var tokenResult = await ExchangeCodeForTokenAsync(BuiltInClientId, GetConfiguredClientSecret(), redirectUri, callback.Code, codeVerifier, ct);
            if (!tokenResult.Success || string.IsNullOrWhiteSpace(tokenResult.AccessToken))
            {
                return new GitHubOAuthResult
                {
                    Success = false,
                    Message = tokenResult.Message
                };
            }

            var user = await GetCurrentUserAsync(tokenResult.AccessToken, ct);
            if (!user.Success)
            {
                return new GitHubOAuthResult
                {
                    Success = false,
                    Message = user.Message
                };
            }

            GitHubSecretStoreService.SetAccessToken(tokenResult.AccessToken);
            GitHubSecretStoreService.SetUserLogin(user.UserLogin);

            return new GitHubOAuthResult
            {
                Success = true,
                UserLogin = user.UserLogin,
                Message = I18n.Format("GitHubOAuth_LoginSuccess", user.UserLogin)
            };
        }

        public static void SignOut()
        {
            GitHubSecretStoreService.ClearAuthorization();
        }

        private static bool HasUsableClientSecret()
        {
            return !string.IsNullOrWhiteSpace(BuiltInClientSecret)
                && !string.Equals(
                    BuiltInClientSecret,
                    "__SET_GITHUB_OAUTH_CLIENT_SECRET_FOR_STORE_RELEASE__",
                    StringComparison.Ordinal);
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FolderRewind", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.Timeout = TimeSpan.FromSeconds(45);
            return client;
        }

        private static Uri BuildAuthorizeUri(string clientId, Uri redirectUri, string state, string codeChallenge)
        {
            var queryParts = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri.ToString(),
                ["scope"] = OAuthScope,
                ["state"] = state,
                ["allow_signup"] = "true",
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256"
            };

            var query = string.Join("&", queryParts.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            return new Uri($"{OAuthAuthorizeUrl}?{query}");
        }

        private static string BuildListenerPrefix(Uri redirectUri)
        {
            var builder = new UriBuilder(redirectUri);
            var path = builder.Path;
            if (!path.EndsWith("/", StringComparison.Ordinal))
            {
                path += "/";
            }

            return $"{builder.Scheme}://{builder.Host}:{builder.Port}{path}";
        }

        private static async Task<OAuthCallbackData> WaitForCallbackAsync(HttpListener listener, CancellationToken ct)
        {
            while (true)
            {
                var contextTask = listener.GetContextAsync();
                var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, ct));
                if (completed != contextTask)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var context = await contextTask;
                try
                {
                    var parameters = ParseQueryString(context.Request.Url?.Query);
                    var callbackData = new OAuthCallbackData
                    {
                        Code = parameters.TryGetValue("code", out var code) ? code : string.Empty,
                        State = parameters.TryGetValue("state", out var state) ? state : string.Empty,
                        Error = parameters.TryGetValue("error", out var error) ? error : string.Empty,
                        ErrorDescription = parameters.TryGetValue("error_description", out var desc) ? desc : string.Empty
                    };

                    await WriteOAuthResponseAsync(context.Response, string.IsNullOrWhiteSpace(callbackData.Error));
                    return callbackData;
                }
                catch
                {
                    try
                    {
                        context.Response.StatusCode = 500;
                        await WriteTextResponseAsync(context.Response, "<html><body>FolderRewind OAuth failed.</body></html>");
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static Dictionary<string, string> ParseQueryString(string? query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query))
            {
                return result;
            }

            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                result[key] = value;
            }

            return result;
        }

        private static async Task WriteOAuthResponseAsync(HttpListenerResponse response, bool success)
        {
            var html = success
                ? "<html><body><h2>FolderRewind</h2><p>GitHub authorization completed. You can close this page.</p></body></html>"
                : "<html><body><h2>FolderRewind</h2><p>GitHub authorization failed. You can close this page and return to the app.</p></body></html>";
            await WriteTextResponseAsync(response, html);
        }

        private static async Task WriteTextResponseAsync(HttpListenerResponse response, string html)
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.LongLength;
            await using var output = response.OutputStream;
            await output.WriteAsync(bytes, 0, bytes.Length);
        }

        private static string CreateRandomToken(int byteCount)
        {
            var bytes = RandomNumberGenerator.GetBytes(byteCount);
            return Base64UrlEncode(bytes);
        }

        private static string CreateCodeChallenge(string verifier)
        {
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
            return Base64UrlEncode(hash);
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static async Task<(bool Success, string Message, string AccessToken)> ExchangeCodeForTokenAsync(
            string clientId,
            string clientSecret,
            Uri redirectUri,
            string code,
            string codeVerifier,
            CancellationToken ct)
        {
            try
            {
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["code"] = code,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri.ToString(),
                    ["code_verifier"] = codeVerifier
                });

                using var request = new HttpRequestMessage(HttpMethod.Post, OAuthAccessTokenUrl);
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = content;

                using var response = await Http.SendAsync(request, ct);
                var payload = await response.Content.ReadAsStringAsync(ct);
                response.EnsureSuccessStatusCode();

                using var json = JsonDocument.Parse(payload);
                if (json.RootElement.TryGetProperty("error", out var error))
                {
                    var description = json.RootElement.TryGetProperty("error_description", out var desc) ? desc.GetString() : string.Empty;
                    return (false, string.IsNullOrWhiteSpace(description) ? error.GetString() ?? I18n.GetString("GitHubOAuth_TokenExchangeFailed") : description, string.Empty);
                }

                var token = json.RootElement.TryGetProperty("access_token", out var accessToken)
                    ? accessToken.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(token))
                {
                    return (false, I18n.GetString("GitHubOAuth_TokenExchangeFailed"), string.Empty);
                }

                return (true, string.Empty, token);
            }
            catch (Exception ex)
            {
                return (false, I18n.Format("GitHubOAuth_TokenExchangeFailedWithReason", ex.Message), string.Empty);
            }
        }

        private static async Task<(bool Success, string Message, string UserLogin)> GetCurrentUserAsync(string accessToken, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{GitHubApiBaseUrl}/user");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

                using var response = await Http.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return (false, I18n.GetString("GitHubOAuth_TokenInvalid"), string.Empty);
                }

                var payload = await response.Content.ReadAsStringAsync(ct);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(payload);
                var login = json.RootElement.TryGetProperty("login", out var loginProp)
                    ? loginProp.GetString() ?? string.Empty
                    : string.Empty;

                return string.IsNullOrWhiteSpace(login)
                    ? (false, I18n.GetString("GitHubOAuth_UserLookupFailed"), string.Empty)
                    : (true, string.Empty, login);
            }
            catch (Exception ex)
            {
                return (false, I18n.Format("GitHubOAuth_UserLookupFailedWithReason", ex.Message), string.Empty);
            }
        }
    }
}
