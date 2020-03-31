using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace SuiDao.Client
{
    public static class HttpHelper
    {
        public static Task<string> PostAsJson(string uri, string strContent)
        {
            using (var handler = new HttpClientHandler() { AutomaticDecompression = DecompressionMethods.None })
            using (var httpclient = new HttpClient(handler))
            {
                httpclient.BaseAddress = new Uri(uri);
                var content = new StringContent(strContent, Encoding.UTF8, "application/json");

                var response = httpclient.PostAsync(uri, content).Result;
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return response.Content.ReadAsStringAsync();
                }
                else
                {
                    throw new Exception(response.ReasonPhrase);
                }
            }
        }
    }
}
