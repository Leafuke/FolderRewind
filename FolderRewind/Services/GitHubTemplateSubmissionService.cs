using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    internal static class GitHubTemplateSubmissionService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly char[] ShareCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();
        private static readonly Regex SlugCleaner = new("[^a-z0-9-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal sealed class SubmissionResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public string ShareCode { get; init; } = string.Empty;
            public string PullRequestUrl { get; init; } = string.Empty;
            public string BranchName { get; init; } = string.Empty;
        }

        private sealed class RepoInfo
        {
            public string Owner { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string DefaultBranch { get; init; } = "main";
            public string HtmlUrl { get; init; } = string.Empty;
        }

        public static async Task<SubmissionResult> SubmitTemplateAsync(
            ConfigTemplate template,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            if (template == null)
            {
                return new SubmissionResult
                {
                    Success = false,
                    Message = I18n.GetString("GitHubSubmit_TemplateNull")
                };
            }

            var validation = TemplateService.ValidateTemplateForOfficialSharing(template);
            if (!validation.Success)
            {
                return new SubmissionResult
                {
                    Success = false,
                    Message = validation.Errors.Count > 0 ? string.Join(Environment.NewLine, validation.Errors) : validation.Message
                };
            }

            if (validation.Warnings.Count > 0)
            {
                LogService.LogWarning(string.Join(Environment.NewLine, validation.Warnings), nameof(GitHubTemplateSubmissionService));
            }

            if (!GitHubOAuthService.IsConfigured(out var configMessage))
            {
                return new SubmissionResult
                {
                    Success = false,
                    Message = configMessage
                };
            }

            var token = GitHubOAuthService.GetStoredAccessToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new SubmissionResult
                {
                    Success = false,
                    Message = I18n.GetString("GitHubOAuth_NotAuthorized")
                };
            }

            var authState = await GitHubOAuthService.GetAuthenticationStateAsync(true, ct);
            if (!authState.IsAuthenticated || string.IsNullOrWhiteSpace(authState.UserLogin))
            {
                return new SubmissionResult
                {
                    Success = false,
                    Message = authState.Message
                };
            }

            progress?.Report(I18n.GetString("GitHubSubmit_Progress_LoadRepo"));
            var upstreamRepo = await GetRepositoryAsync(token, OfficialTemplateService.OfficialRepoOwner, OfficialTemplateService.OfficialRepoName, ct);
            if (upstreamRepo == null)
            {
                return new SubmissionResult
                {
                    Success = false,
                    Message = I18n.GetString("GitHubSubmit_RepoLookupFailed")
                };
            }

            progress?.Report(I18n.GetString("GitHubSubmit_Progress_LoadIndex"));
            var indexResult = await OfficialTemplateService.GetIndexAsync();
            if (!indexResult.Success)
            {
                return new SubmissionResult
                {
                    Success = false,
                    Message = indexResult.Message
                };
            }

            var shareCode = ResolveShareCode(template, indexResult.Templates);
            var branchName = $"template/{shareCode}-{BuildSlug(template.GameName, template.Name)}";
            var tempPath = Path.Combine(Path.GetTempPath(), "FolderRewind", "TemplateSubmit", $"{shareCode}{TemplateService.ShareFileExtension}");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

            var originalShareCode = template.ShareCode;
            try
            {
                template.ShareCode = shareCode;
                progress?.Report(I18n.GetString("GitHubSubmit_Progress_ExportTemplate"));
                if (!TemplateService.ExportTemplate(template.Id, tempPath, out var exportMessage))
                {
                    template.ShareCode = originalShareCode;
                    return new SubmissionResult
                    {
                        Success = false,
                        Message = exportMessage
                    };
                }

                var templateJson = await File.ReadAllTextAsync(tempPath, ct);

                progress?.Report(I18n.GetString("GitHubSubmit_Progress_EnsureFork"));
                if (!await EnsureForkExistsAsync(token, authState.UserLogin, upstreamRepo.Owner, upstreamRepo.Name, ct))
                {
                    template.ShareCode = originalShareCode;
                    return new SubmissionResult
                    {
                        Success = false,
                        Message = I18n.GetString("GitHubSubmit_ForkFailed")
                    };
                }

                progress?.Report(I18n.GetString("GitHubSubmit_Progress_SyncFork"));
                await TrySyncForkAsync(token, authState.UserLogin, upstreamRepo.Name, upstreamRepo.DefaultBranch, ct);

                progress?.Report(I18n.GetString("GitHubSubmit_Progress_CreateBranch"));
                var forkBaseSha = await GetBranchShaAsync(token, authState.UserLogin, upstreamRepo.Name, upstreamRepo.DefaultBranch, ct);
                if (string.IsNullOrWhiteSpace(forkBaseSha))
                {
                    template.ShareCode = originalShareCode;
                    return new SubmissionResult
                    {
                        Success = false,
                        Message = I18n.GetString("GitHubSubmit_BaseBranchLookupFailed")
                    };
                }

                branchName = await EnsureSubmissionBranchAsync(token, authState.UserLogin, upstreamRepo.Name, branchName, forkBaseSha, ct);

                progress?.Report(I18n.GetString("GitHubSubmit_Progress_CommitTemplate"));
                var templatePath = $"templates/{shareCode}.json";
                var existingSha = await TryGetContentShaAsync(token, authState.UserLogin, upstreamRepo.Name, templatePath, branchName, ct);
                var commitMessage = existingSha == null
                    ? $"Add official template {shareCode} for {template.Name}"
                    : $"Update official template {shareCode} for {template.Name}";

                var contentUpdated = await UpsertFileAsync(
                    token,
                    authState.UserLogin,
                    upstreamRepo.Name,
                    templatePath,
                    branchName,
                    templateJson,
                    commitMessage,
                    existingSha,
                    ct);

                if (!contentUpdated)
                {
                    template.ShareCode = originalShareCode;
                    return new SubmissionResult
                    {
                        Success = false,
                        Message = I18n.GetString("GitHubSubmit_CommitFailed")
                    };
                }

                progress?.Report(I18n.GetString("GitHubSubmit_Progress_CreatePr"));
                var pr = await CreatePullRequestAsync(
                    token,
                    upstreamRepo.Owner,
                    upstreamRepo.Name,
                    authState.UserLogin,
                    branchName,
                    upstreamRepo.DefaultBranch,
                    BuildPullRequestTitle(template, shareCode),
                    BuildPullRequestBody(template, shareCode, validation.Warnings),
                    ct);

                if (!pr.Success)
                {
                    template.ShareCode = originalShareCode;
                    return new SubmissionResult
                    {
                        Success = false,
                        Message = pr.Message
                    };
                }

                template.ShareCode = shareCode;
                ConfigService.Save();

                return new SubmissionResult
                {
                    Success = true,
                    Message = string.IsNullOrWhiteSpace(pr.Message)
                        ? I18n.Format("GitHubSubmit_Success", shareCode)
                        : pr.Message,
                    ShareCode = shareCode,
                    PullRequestUrl = pr.PullRequestUrl,
                    BranchName = branchName
                };
            }
            catch (Exception ex)
            {
                template.ShareCode = originalShareCode;
                return new SubmissionResult
                {
                    Success = false,
                    Message = I18n.Format("GitHubSubmit_FailedWithReason", ex.Message)
                };
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FolderRewind", "1.0"));
            client.Timeout = TimeSpan.FromSeconds(60);
            return client;
        }

        private static string ResolveShareCode(ConfigTemplate template, IReadOnlyList<RemoteTemplateIndexItem> items)
        {
            var templateId = template.TemplateId;
            var byTemplateId = items.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.TemplateId)
                && string.Equals(item.TemplateId, templateId, StringComparison.OrdinalIgnoreCase));
            if (byTemplateId != null)
            {
                return byTemplateId.ShareCode;
            }

            if (OfficialTemplateService.IsValidShareCode(template.ShareCode))
            {
                var collision = items.FirstOrDefault(item =>
                    string.Equals(item.ShareCode, template.ShareCode, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(item.TemplateId, templateId, StringComparison.OrdinalIgnoreCase));
                if (collision == null)
                {
                    return template.ShareCode.Trim().ToUpperInvariant();
                }
            }

            var usedCodes = new HashSet<string>(items.Select(item => item.ShareCode), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 64; i++)
            {
                var code = GenerateShareCode();
                if (usedCodes.Add(code))
                {
                    return code;
                }
            }

            throw new InvalidOperationException(I18n.GetString("GitHubSubmit_ShareCodeExhausted"));
        }

        private static string GenerateShareCode()
        {
            Span<char> chars = stackalloc char[5];
            Span<byte> bytes = stackalloc byte[5];
            RandomNumberGenerator.Fill(bytes);
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = ShareCodeAlphabet[bytes[i] % ShareCodeAlphabet.Length];
            }

            return new string(chars);
        }

        private static string BuildSlug(string primary, string fallback)
        {
            var source = string.IsNullOrWhiteSpace(primary) ? fallback : primary;
            var lower = source.Trim().ToLowerInvariant().Replace(' ', '-');
            lower = SlugCleaner.Replace(lower, "-");
            lower = lower.Trim('-');
            if (string.IsNullOrWhiteSpace(lower))
            {
                lower = "template";
            }

            return lower.Length <= 32 ? lower : lower[..32];
        }

        private static async Task<RepoInfo?> GetRepositoryAsync(string token, string owner, string repo, CancellationToken ct)
        {
            using var request = CreateGitHubRequest(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}", token);
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(payload);
            return new RepoInfo
            {
                Owner = owner,
                Name = repo,
                DefaultBranch = json.RootElement.TryGetProperty("default_branch", out var branch) ? branch.GetString() ?? "main" : "main",
                HtmlUrl = json.RootElement.TryGetProperty("html_url", out var url) ? url.GetString() ?? string.Empty : string.Empty
            };
        }

        private static async Task<bool> EnsureForkExistsAsync(string token, string login, string upstreamOwner, string repo, CancellationToken ct)
        {
            var existing = await GetRepositoryAsync(token, login, repo, ct);
            if (existing != null)
            {
                return true;
            }

            using var request = CreateGitHubRequest(HttpMethod.Post, $"https://api.github.com/repos/{upstreamOwner}/{repo}/forks", token);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Accepted && response.StatusCode != HttpStatusCode.Created)
            {
                return false;
            }

            for (int attempt = 0; attempt < 15; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                var fork = await GetRepositoryAsync(token, login, repo, ct);
                if (fork != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task TrySyncForkAsync(string token, string login, string repo, string branch, CancellationToken ct)
        {
            try
            {
                using var request = CreateGitHubRequest(HttpMethod.Post, $"https://api.github.com/repos/{login}/{repo}/merge-upstream", token);
                request.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["branch"] = branch
                }), Encoding.UTF8, "application/json");
                using var response = await Http.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.NoContent
                    || response.StatusCode == HttpStatusCode.Created
                    || response.StatusCode == HttpStatusCode.OK
                    || response.StatusCode == HttpStatusCode.Conflict)
                {
                    return;
                }
            }
            catch
            {
            }
        }

        private static async Task<string> GetBranchShaAsync(string token, string owner, string repo, string branch, CancellationToken ct)
        {
            using var request = CreateGitHubRequest(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/git/ref/heads/{Uri.EscapeDataString(branch)}", token);
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("object", out var obj)
                && obj.TryGetProperty("sha", out var sha))
            {
                return sha.GetString() ?? string.Empty;
            }

            return string.Empty;
        }

        private static async Task<string> EnsureSubmissionBranchAsync(string token, string owner, string repo, string branchName, string baseSha, CancellationToken ct)
        {
            var candidate = branchName;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                using var request = CreateGitHubRequest(HttpMethod.Post, $"https://api.github.com/repos/{owner}/{repo}/git/refs", token);
                request.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["ref"] = $"refs/heads/{candidate}",
                    ["sha"] = baseSha
                }), Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.Created)
                {
                    return candidate;
                }

                if (response.StatusCode != HttpStatusCode.UnprocessableEntity)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? I18n.GetString("GitHubSubmit_BranchCreateFailed") : body);
                }

                candidate = $"{branchName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            }

            throw new InvalidOperationException(I18n.GetString("GitHubSubmit_BranchCreateFailed"));
        }

        private static async Task<string?> TryGetContentShaAsync(string token, string owner, string repo, string path, string branch, CancellationToken ct)
        {
            using var request = CreateGitHubRequest(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/contents/{path}?ref={Uri.EscapeDataString(branch)}", token);
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
        }

        private static async Task<bool> UpsertFileAsync(
            string token,
            string owner,
            string repo,
            string path,
            string branch,
            string textContent,
            string commitMessage,
            string? existingSha,
            CancellationToken ct)
        {
            using var request = CreateGitHubRequest(HttpMethod.Put, $"https://api.github.com/repos/{owner}/{repo}/contents/{path}", token);
            var body = new Dictionary<string, object?>
            {
                ["message"] = commitMessage,
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(textContent)),
                ["branch"] = branch
            };
            if (!string.IsNullOrWhiteSpace(existingSha))
            {
                body["sha"] = existingSha;
            }

            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }

        private static string BuildPullRequestTitle(ConfigTemplate template, string shareCode)
        {
            var gameText = string.IsNullOrWhiteSpace(template.GameName) ? template.Name : template.GameName;
            return $"Add template {shareCode}: {gameText}";
        }

        private static string BuildPullRequestBody(ConfigTemplate template, string shareCode, IReadOnlyList<string> warnings)
        {
            var builder = new StringBuilder();
            builder.AppendLine("## Template Metadata");
            builder.AppendLine($"- Share code: `{shareCode}`");
            builder.AppendLine($"- Template ID: `{template.TemplateId}`");
            builder.AppendLine($"- Template name: {template.Name}");
            builder.AppendLine($"- Game name: {(string.IsNullOrWhiteSpace(template.GameName) ? "-" : template.GameName)}");
            builder.AppendLine($"- Author: {(string.IsNullOrWhiteSpace(template.Author) ? "Anonymous" : template.Author)}");
            builder.AppendLine($"- Version: {template.Version}");
            builder.AppendLine($"- Config type: {template.BaseConfigType}");
            builder.AppendLine($"- Path rules: {template.PathRules?.Count ?? 0}");

            if (template.RequiredPluginIds != null && template.RequiredPluginIds.Count > 0)
            {
                builder.AppendLine($"- Required plugins: {string.Join(", ", template.RequiredPluginIds)}");
            }

            if (!string.IsNullOrWhiteSpace(template.Description))
            {
                builder.AppendLine();
                builder.AppendLine("## Description");
                builder.AppendLine(template.Description.Trim());
            }

            if (warnings.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## Validation Warnings");
                foreach (var warning in warnings)
                {
                    builder.AppendLine($"- {warning}");
                }
            }

            return builder.ToString().Trim();
        }

        private static async Task<(bool Success, string Message, string PullRequestUrl)> CreatePullRequestAsync(
            string token,
            string upstreamOwner,
            string repo,
            string forkOwner,
            string branchName,
            string baseBranch,
            string title,
            string body,
            CancellationToken ct)
        {
            using var request = CreateGitHubRequest(HttpMethod.Post, $"https://api.github.com/repos/{upstreamOwner}/{repo}/pulls", token);
            request.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["title"] = title,
                ["head"] = $"{forkOwner}:{branchName}",
                ["base"] = baseBranch,
                ["body"] = body
            }), Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(payload);
                var prUrl = json.RootElement.TryGetProperty("html_url", out var html)
                    ? html.GetString() ?? string.Empty
                    : string.Empty;
                return (true, I18n.Format("GitHubSubmit_PullRequestCreated", prUrl), prUrl);
            }

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var existing = await TryFindExistingPullRequestAsync(token, upstreamOwner, repo, forkOwner, branchName, ct);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return (true, I18n.Format("GitHubSubmit_PullRequestExists", existing), existing);
                }
            }

            return (false, string.IsNullOrWhiteSpace(payload) ? I18n.GetString("GitHubSubmit_PullRequestFailed") : payload, string.Empty);
        }

        private static async Task<string> TryFindExistingPullRequestAsync(string token, string upstreamOwner, string repo, string forkOwner, string branchName, CancellationToken ct)
        {
            using var request = CreateGitHubRequest(HttpMethod.Get, $"https://api.github.com/repos/{upstreamOwner}/{repo}/pulls?state=open&head={Uri.EscapeDataString(forkOwner + ":" + branchName)}", token);
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0)
            {
                var first = json.RootElement[0];
                if (first.TryGetProperty("html_url", out var html))
                {
                    return html.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static HttpRequestMessage CreateGitHubRequest(HttpMethod method, string url, string token)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            return request;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
