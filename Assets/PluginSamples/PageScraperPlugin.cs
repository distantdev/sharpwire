using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace Plugins
{
    public class WebTools
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        [Description("Fetches a web page and returns its text content, stripped of HTML tags and scripts.")]
        public async Task<string> ScrapePage([Description("The full URL to scrape (including http/https).")] string url)
        {
            try
            {
                var response = await _httpClient.GetStringAsync(url);
                
                // Remove scripts, styles, and other non-content tags
                var clean = Regex.Replace(response, @"<(script|style|svg|canvas|head|footer|nav)[^>]*?>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                
                // Remove all other HTML tags
                clean = Regex.Replace(clean, @"<[^>]*?>", " ");
                
                // Collapse multiple spaces and newlines
                clean = Regex.Replace(clean, @"\s+", " ").Trim();

                // Limit length to keep context usage efficient
                return clean.Length > 6000 ? clean.Substring(0, 6000) + "... [Truncated]" : clean;
            }
            catch (Exception ex)
            {
                return $"Error scraping page: {ex.Message}";
            }
        }
    }
}
