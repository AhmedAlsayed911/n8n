using Generator.Models;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Generator.Controllers
{
    public class HomeController : Controller
    {
        private const string AiWebhookUrl = "https://ahmedsayedxz.app.n8n.cloud/webhook-test/ai-endpoint";
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<HomeController> _logger;

        public HomeController(IHttpClientFactory httpClientFactory, ILogger<HomeController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AskAI([FromForm] string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return BadRequest(new AiResponse
                {
                    Result = string.Empty,
                    Status = "Please enter a prompt."
                });
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                var payload = JsonSerializer.Serialize(new { prompt });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(AiWebhookUrl, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("AskAI request failed with status code {StatusCode}. Response: {Response}", (int)response.StatusCode, responseBody);
                    return StatusCode((int)response.StatusCode, new AiResponse
                    {
                        Result = string.Empty,
                        Status = "Something went wrong"
                    });
                }

                if (string.IsNullOrWhiteSpace(responseBody))
                {
                    return Ok(new AiResponse
                    {
                        Result = "No response received",
                        Status = string.Empty
                    });
                }

                var apiResponse = JsonSerializer.Deserialize<AiResponse>(responseBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (apiResponse is null)
                {
                    apiResponse = new AiResponse();
                }

                if (string.IsNullOrWhiteSpace(apiResponse.Result))
                {
                    apiResponse.Result = ExtractResponseText(responseBody);
                }

                if (string.IsNullOrWhiteSpace(apiResponse.Result) ||
                    string.Equals(apiResponse.Result, "Response received, but no readable message was found.", StringComparison.OrdinalIgnoreCase))
                {
                    apiResponse.Result = "No response received";
                }

                if (string.IsNullOrWhiteSpace(apiResponse.Status))
                {
                    apiResponse.Status = ExtractStatusText(responseBody);
                }

                return Ok(apiResponse);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error calling AI webhook endpoint.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new AiResponse
                {
                    Result = string.Empty,
                    Status = "Something went wrong"
                });
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Invalid JSON from AI service.");
                return Ok(new AiResponse
                {
                    Result = "No response received",
                    Status = "Something went wrong"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during AskAI call.");
                return StatusCode(StatusCodes.Status500InternalServerError, new AiResponse
                {
                    Result = string.Empty,
                    Status = "Something went wrong"
                });
            }
        }

        private static string ExtractResponseText(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (TryGetOpenAiContent(root, out var chatContent))
            {
                return chatContent;
            }

            if (TryExtractBestText(root, out var extracted))
            {
                return extracted;
            }

            if (root.ValueKind == JsonValueKind.String)
            {
                return root.GetString() ?? string.Empty;
            }

            return "Response received, but no readable message was found.";
        }

        private static bool TryExtractBestText(JsonElement root, out string value)
        {
            value = string.Empty;

            if (root.ValueKind == JsonValueKind.String)
            {
                var direct = root.GetString() ?? string.Empty;
                if (IsHumanReadableText(direct))
                {
                    value = direct;
                    return true;
                }
            }

            var preferredKeys = new[] { "response", "answer", "message", "output", "content", "text", "subject", "body", "result", "status" };

            foreach (var key in preferredKeys)
            {
                if (TryGetStringProperty(root, key, out var candidate))
                {
                    value = candidate;
                    return true;
                }
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var candidate = property.Value.GetString() ?? string.Empty;
                        if (!IsTechnicalKey(property.Name) && IsHumanReadableText(candidate))
                        {
                            value = candidate;
                            return true;
                        }
                    }

                    if (TryExtractBestText(property.Value, out var nested))
                    {
                        value = nested;
                        return true;
                    }
                }
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (TryExtractBestText(item, out var nested))
                    {
                        value = nested;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetOpenAiContent(JsonElement root, out string content)
        {
            content = string.Empty;

            // Try to find choices at current level
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];

                if (firstChoice.ValueKind == JsonValueKind.Object &&
                    firstChoice.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out var choiceContent) &&
                    choiceContent.ValueKind == JsonValueKind.String)
                {
                    content = choiceContent.GetString() ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(content);
                }

                if (firstChoice.ValueKind == JsonValueKind.Object &&
                    firstChoice.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    content = text.GetString() ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(content);
                }
            }

            // Recursively search in nested objects (handles deeply nested webhook responses)
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (TryGetOpenAiContent(property.Value, out content))
                    {
                        return true;
                    }
                }
            }

            // Recursively search in arrays
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (TryGetOpenAiContent(item, out content))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetStringProperty(JsonElement root, string key, out string value)
        {
            value = string.Empty;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var property))
            {
                return false;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString() ?? string.Empty;
                return IsHumanReadableText(value);
            }

            if (property.ValueKind == JsonValueKind.Object && TryGetStringProperty(property, "content", out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.Object && TryExtractBestText(property, out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        value = item.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return true;
                        }
                    }

                    if (TryExtractBestText(item, out value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsTechnicalKey(string key)
        {
            var technicalKeys = new[] { "id", "threadId", "labelIds", "labels", "createdAt", "updatedAt", "timestamp" };
            return technicalKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsHumanReadableText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();

            if (trimmed.Length < 2)
            {
                return false;
            }

            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                return false;
            }

            return true;
        }

        private static string ExtractStatusText(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                return status.GetString() ?? string.Empty;
            }

            return string.Empty;
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
