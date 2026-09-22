using HRSystem.Models;
using Microsoft.AspNetCore.Http;
using System;
using System.Net;
using System.Threading.Tasks;

namespace HRSystem.Services
{
    public class SessionLogMiddleware
    {
        private readonly RequestDelegate _next;

        public SessionLogMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, HRContext dbContext)
        {
            await _next(context);

            // Log only authenticated users to keep the logs extremely clean and relevant
            if (context.User.Identity?.IsAuthenticated == true)
            {
                var request = context.Request;
                string path = request.Path.ToString().ToLower();

                // Skip static assets, APIs, and high-frequency AJAX background requests to prevent DB bloat
                if (!path.Contains("/lib/") && !path.Contains("/css/") && !path.Contains("/js/") &&
                    !path.Contains("/getdevicestats") && !path.Contains("/index?") &&
                    !path.Contains("favicon.ico") && !path.Contains("/api/"))
                {
                    try
                    {
                        string email = context.User.Identity.Name;
                        string ipAddress = context.Connection.RemoteIpAddress?.ToString();
                        
                        // Normalize localhost loopback address
                        if (ipAddress == "::1") ipAddress = "127.0.0.1";

                        // Get PC/Computer Name using Reverse DNS asynchronously
                        string computerName = "Unknown";
                        if (!string.IsNullOrEmpty(ipAddress) && ipAddress != "127.0.0.1")
                        {
                            try
                            {
                                // We perform this network resolution safely to get the actual PC hostname on the network
                                var hostEntry = await Dns.GetHostEntryAsync(ipAddress);
                                computerName = hostEntry.HostName;
                            }
                            catch
                            {
                                computerName = "Offline / Private PC";
                            }
                        }
                        else if (ipAddress == "127.0.0.1")
                        {
                            computerName = "Localhost Server";
                        }

                        // Extract clean OS & Browser details from User-Agent
                        string userAgent = request.Headers["User-Agent"].ToString();
                        string deviceDetails = ParseUserAgent(userAgent);

                        string actionAccessed = $"{request.Method} {request.Path}";

                        // Save the audit log to the database
                        dbContext.UserSessionLogs.Add(new UserSessionLog
                        {
                            Email = email ?? "Unknown User",
                            IpAddress = ipAddress ?? "Unknown IP",
                            ComputerName = computerName ?? "Unknown Host",
                            DeviceDetails = deviceDetails ?? "Unknown Device",
                            AccessTime = DateTime.Now,
                            ActionAccessed = actionAccessed ?? "Unknown Action"
                        });

                        await dbContext.SaveChangesAsync();
                    }
                    catch
                    {
                        // Catch all to make sure logging never disrupts the main web application workflow
                    }
                }
            }
        }

        private string ParseUserAgent(string userAgent)
        {
            if (string.IsNullOrEmpty(userAgent)) return "Unknown Device";

            string os = "Unknown OS";
            string browser = "Unknown Browser";

            // OS Detection
            if (userAgent.Contains("Windows")) os = "Windows PC";
            else if (userAgent.Contains("Android")) os = "Android Device";
            else if (userAgent.Contains("iPhone")) os = "iPhone Mobile";
            else if (userAgent.Contains("iPad")) os = "iPad Tablet";
            else if (userAgent.Contains("Macintosh")) os = "Apple Mac";
            else if (userAgent.Contains("Linux")) os = "Linux PC";

            // Browser Detection
            if (userAgent.Contains("Edg/")) browser = "Microsoft Edge";
            else if (userAgent.Contains("Chrome") && !userAgent.Contains("Chromium")) browser = "Google Chrome";
            else if (userAgent.Contains("Safari") && !userAgent.Contains("Chrome")) browser = "Apple Safari";
            else if (userAgent.Contains("Firefox")) browser = "Mozilla Firefox";

            return $"{os} ({browser})";
        }
    }
}
