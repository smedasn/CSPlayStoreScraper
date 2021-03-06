﻿using System;
using System.Linq;
using WebUtilsLib;
using System.Text.RegularExpressions;
using System.Text;
using System.Globalization;
using System.Net;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Security.Permissions;
using System.Security;
using System.ComponentModel;
using System.Reflection;
using PlayStoreScraper.Exporters;
using PlayStoreScraper.Models;

namespace PlayStoreScraper
{
    class PlayStoreScraper
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // Response Parser
        private static PlayStoreParser parser = new PlayStoreParser();

        /// <summary>
        /// Crawl Play Store based on keywords given and export the result to CSV
        /// </summary>
        /// <param name="keywords">Array of keywords</param>
        /// <param name="exporter">Exporter class for exporting. If not null, the IExporter.Export() will be called.</param>
        /// <param name="maxAppUrls">Maximum Scraped App Urls for each keyword. To scrape without limit, set the value to 0</param>
        /// <param name="downloadDelay">Download Delay in milliseconds</param>
        /// <param name="writeCallback">Callback method for writing the App Data</param>
        public static void Crawl(string[] keywords, IExporter exporter = null, int maxAppUrls = 0, int downloadDelay = 0,
            Action<AppModel> writeCallback = null)
        {
            if (exporter != null)
            {
                exporter.Open();
            }

            // Collect App Urls from keywords
            foreach (string keyword in keywords)
            {
                ISet<string> urls = CollectAppUrls(keyword, maxAppUrls);

                // Apply download delay
                if (downloadDelay > 0)
                {
                    Thread.Sleep(downloadDelay);
                }

                // Parse each of App Urls found
                ParseAppUrls(urls, downloadDelay, exporter, writeCallback);
            }

            if (exporter != null)
            {
                exporter.Close();
            }
        }

        private static ISet<string> CollectAppUrls(string searchField, int maxAppUrls)
        {
            ISet<string> resultUrls = new HashSet<string>();

            log.Info("Crawling Search Term : [ " + searchField + " ]");

            string crawlUrl = String.Format(Consts.CRAWL_URL, searchField);

            // HTML Response
            string response;

            // Executing Web Requests
            using (WebRequests server = new WebRequests())
            {
                // Creating Request Object
                server.Host = Consts.HOST;

                int insertedAppCount = 0;
                int skippedAppCount = 0;
                int errorsCount = 0;

                string postData = Consts.INITIAL_POST_DATA;

                do
                {
                    // Executing Request
                    response = server.Post(crawlUrl, postData);

                    // Checking Server Status
                    if (server.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        log.Error("Http Error - Status Code: " + server.StatusCode);

                        errorsCount++;

                        if (errorsCount > Consts.MAX_REQUEST_ERRORS)
                        {
                            log.Info("Crawl Stopped: MAX_REQUEST_ERRORS reached");
                            break;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    // Parsing Links out of Html Page
                    foreach (string url in parser.ParseAppUrls(response))
                    {
                        if (!resultUrls.Contains(url))
                        {
                            resultUrls.Add(url);

                            log.Info("Inserted App: " + url);

                            ++insertedAppCount;

                            if (maxAppUrls > 0 && insertedAppCount >= maxAppUrls)
                            {
                                goto exit;
                            }
                        }
                        else
                        {
                            ++skippedAppCount;
                            log.Info("Duplicated App. Skipped: " + url);
                        }
                    }

                    // Get pagTok value that will be used to fetch next stream data.
                    // If not found, that means we have reached the end of stream.
                    string pagTok = getPageToken(response);
                    if (pagTok.Length == 0)
                    {
                        break;
                    }

                    // Build the next post data
                    postData = String.Format(Consts.POST_DATA, pagTok);

                } while (true);

            exit:
                log.Info("Inserted App Count: " + insertedAppCount);
                log.Info("Skipped App Count: " + skippedAppCount);
                log.Info("Error Count: " + errorsCount + "\n");
            }

            return resultUrls;
        }

        private static void ParseAppUrls(ISet<string> urls, int downloadDelay = 0, IExporter exporter = null,
            Action<AppModel> writeCallback = null)
        {
            log.Info("Parsing App URLs...");

            int parsedAppCount = 0;

            // Retry Counter (Used for exponential wait increasing logic)
            int retryCounter = 0;

            // Creating Instance of Web Requests Server
            WebRequests server = new WebRequests();

            foreach (string url in urls)
            {
                try
                {
                    // Building APP URL
                    string appUrl = Consts.APP_URL_PREFIX + url;

                    // Configuring server and Issuing Request
                    server.Headers.Add(Consts.ACCEPT_LANGUAGE);
                    server.Host = Consts.HOST;
                    server.Encoding = "utf-8";
                    server.EncodingDetection = WebRequests.CharsetDetection.DefaultCharset;
                    string response = server.Get(appUrl);

                    // Sanity Check
                    if (String.IsNullOrEmpty(response) || server.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        log.Info("Error opening app page : " + appUrl);

                        // Renewing WebRequest Object to get rid of Cookies
                        server = new WebRequests();

                        // Inc. retry counter
                        retryCounter++;

                        log.Info("Retrying:" + retryCounter);

                        // Checking for maximum retry count
                        double waitTime;
                        if (retryCounter >= 11)
                        {
                            waitTime = TimeSpan.FromMinutes(35).TotalMilliseconds;
                        }
                        else
                        {
                            // Calculating next wait time ( 2 ^ retryCounter seconds)
                            waitTime = TimeSpan.FromSeconds(Math.Pow(2, retryCounter)).TotalMilliseconds;
                        }

                        // Hiccup to avoid google blocking connections in case of heavy traffic from the same IP
                        Thread.Sleep(Convert.ToInt32(waitTime));
                    }
                    else
                    {
                        // Reseting retry counter
                        retryCounter = 0;

                        // Parsing App Data
                        AppModel parsedApp = parser.ParseAppPage(response, appUrl);

                        // Export the App Data
                        if (exporter != null)
                        {
                            log.Info("Parsed App: " + parsedApp.Name);

                            exporter.Write(parsedApp);
                        }

                        // Pass the App Data to callback method
                        if (writeCallback != null)
                        {
                            writeCallback(parsedApp);
                        }

                        // Default action is print to screen
                        if (exporter == null && writeCallback == null)
                        {
                            Console.WriteLine(parsedApp);
                        }

                        ++parsedAppCount;

                        // Apply download delay
                        if (downloadDelay > 0)
                        {
                            Thread.Sleep(downloadDelay);
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }
            }

            log.Info("Finished. Parsed App count: " + parsedAppCount + "\n");
        }
        
        /// <summary>
        /// Get Page Token for play store streaming search result.
        /// </summary>
        /// <param name="response">Response body</param>
        /// <returns>Page Token</returns>
        protected static string getPageToken(string response)
        {
            string pagTok = "";
            string regex = @"'\[.*\\42((?:.(?!\\42))*:S:.*?)\\42.*\]\\n'";
            Match match = Regex.Match(response, regex);
            if (match.Success)
            {
                pagTok = DecodeEncodedNonAsciiCharacters(match.Groups[1].Value, true);
            }
            return pagTok;
        }

        protected static string EncodeNonAsciiCharacters(string value)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in value)
            {
                if (c > 127)
                {
                    // This character is too big for ASCII
                    string encodedValue = "\\u" + ((int)c).ToString("x4");
                    sb.Append(encodedValue);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        protected static string DecodeEncodedNonAsciiCharacters(string value, bool isDoubleSlash = false)
        {
            string regex = @"\\u(?<Value>[a-zA-Z0-9]{4})";
            if (isDoubleSlash)
            {
                regex = @"\\" + regex;
            }

            return Regex.Replace(
                value,
                regex,
                m =>
                {
                    return ((char)int.Parse(m.Groups["Value"].Value, NumberStyles.HexNumber)).ToString();
                });
        }
    }
}
