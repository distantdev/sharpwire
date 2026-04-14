using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.ComponentModel;
using Sharpwire.Core.MetaToolbox;
using System.Collections.Generic; // Added for Dictionary

namespace Plugins
{
    [Description("Provides methods to search the web using the Brave Search API.")]
    [PluginSettings("Brave Search Plugin Settings")]
    public class BraveSearchTools : IPluginWithSettings
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        
        [PluginSetting("Brave Search API Key", "Your API key for Brave Search. You can get one from https://brave.com/search/api.", true)]
        public string BraveSearchApiKey { get; set; } = "YOUR_BRAVE_SEARCH_API_KEY";

        public BraveSearchTools()
        {
            // Settings will be loaded via OnSettingsLoaded, no need for custom LoadSettings
        }

        public void OnSettingsLoaded(Dictionary<string, object> settings)
        {
            // The properties will already be populated by the settings system.
            // We can add any post-load logic here if needed.
        }

        [Description("Searches the web for the specified query using the Brave Search API and returns the results in JSON format.")]
        public async Task<string> Search([Description("The search query.")] string query)
        {
            string apiKey = BraveSearchApiKey;

            if (string.IsNullOrEmpty(apiKey) || apiKey == "YOUR_BRAVE_SEARCH_API_KEY")
            {
                return "Error: Brave Search API key is not configured. Please update 'Brave Search API Key' in the plugin settings.";
            }

            try
            {
                string url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}";
                
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Add("X-Subscription-Token", apiKey);
                    request.Headers.Add("Accept", "application/json");

                    using (var response = await _httpClient.SendAsync(request))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            return await response.Content.ReadAsStringAsync();
                        }
                        else
                        {
                            string errorContent = await response.Content.ReadAsStringAsync();
                            return $"Error: Brave Search API request failed with status code {response.StatusCode}. Response: {errorContent}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"Error: An exception occurred while calling the Brave Search API: {ex.Message}";
            }
        }
    }
}
