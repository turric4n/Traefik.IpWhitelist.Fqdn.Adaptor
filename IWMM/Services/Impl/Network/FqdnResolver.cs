﻿using IWMM.Services.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace IWMM.Services.Impl.Network
{
    public class FqdnResolver : IFqdnResolver
    {
#if Windows
        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        static extern UInt32 DnsFlushResolverCache();

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCacheEntry_A")]
        public static extern int DnsFlushResolverCacheEntry(string hostName);
#endif

        private const string FqdnResolverExceptionMessage = "Couldn't resolve FQDN : ";
        private const string IpV6SubnetPrefix = "::ffff:";

        private readonly ILogger<FqdnResolver> _logger;

        public FqdnResolver(ILogger<FqdnResolver> logger)
        {
            _logger = logger;
        }

        public async Task<IEnumerable<string>> GetIpAddressesAsync(string fqdn)
        {
            try
            {
                ServicePointManager.DnsRefreshTimeout = 0;
#if Windows
                DnsFlushResolverCache();
#endif

                var gotIps = await Dns.GetHostAddressesAsync(fqdn, AddressFamily.InterNetwork);

                // Convert list of IP addresses to array of string
                var gotIp = gotIps
                    .Select(x => x.ToString())
                    .ToArray();

                if (gotIp == null)
                {
                    gotIps = await Dns.GetHostAddressesAsync(fqdn, AddressFamily.InterNetworkV6);
                    gotIp = gotIps
                        .Select(x => x.ToString())
                        .ToArray();
                }

                if (gotIp.Contains(IpV6SubnetPrefix))
                {
                    // Remove IPv6 subnet prefix
                    gotIp = gotIp
                        .Select(x => x.Replace(IpV6SubnetPrefix, string.Empty))
                        .ToArray();
                }

                return gotIp;
            }
            catch (Exception e)
            {
                var message = $"{FqdnResolverExceptionMessage} {fqdn} -> {e.Message}";

                _logger.LogError(message);

                throw new FqdnResolverException(message);
            }
        }
    }
}