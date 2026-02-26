using Newtonsoft.Json;
using System;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Utils
{
    internal static class PenumbraApi
    {
        private const int TIMEOUT_MS = 500;

        private static readonly HttpClient s_client = new()
        {
            Timeout = TimeSpan.FromMilliseconds(TIMEOUT_MS),
        };

        /// <summary>
        /// Calls /reloadmod on the Penumbra API.
        /// </summary>
        /// <returns></returns>
        public static async Task<bool> ReloadMod(string path, string name = null)
        {
            Dictionary<string, string> args = new Dictionary<string, string>();

            if (name != null)
            {
                args.Add("Name", name);
            }
            if (path != null)
            {
                args.Add("Path", path);
            }

            return await Request("/reloadmod", args);
        }

        private static HttpClient _Client = new HttpClient() { BaseAddress = new System.Uri("http://localhost:42069") };

        private static async Task<bool> Request(string urlPath, object data = null)
        {
            data = data == null ? new object() : data;
            return await Task.Run(async () => {
                try
                {
                    using StringContent jsonContent = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await _Client.PostAsync("api/" + urlPath, jsonContent);

                    response.EnsureSuccessStatusCode();

                    return true;
                }
                catch (Exception ex)
                {
                    return false;
                    //throw;
                }
            });
        }
    }
}
