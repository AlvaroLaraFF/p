using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Net
{
    public sealed class IgdMapResult
    {
        public int ExternalPort;
        /// <summary>Lease actually granted in seconds (0 = permanent: the router only supported permanent mappings).</summary>
        public int LeaseSeconds;
        public bool UdpMapped;
        /// <summary>true if the router said OK to AddPortMapping but GetSpecificPortMappingEntry does not show it (714): it is taken as good, but could not be verified.</summary>
        public bool Unverified;
    }

    /// <summary>
    /// Mapping policy over an IGD: preferred external port and alternatives on conflicts (718), lease 0 if the router only supports permanent ones (725; also
    /// 402/501 with a lease), verification with GetSpecificPortMappingEntry, spare UDP (best effort) and deletion.
    /// </summary>
    public static class IgdMapper
    {
        /// <summary>External ports to try, in order: the preferred one, the next 3 and then random high ports.</summary>
        public static List<int> Candidates(int preferred, int max, Random rnd)
        {
            var list = new List<int>();
            for (int i = 0; i < 4 && list.Count < max; i++)
                if (preferred + i <= 65535) list.Add(preferred + i);
            int guard = 0;
            while (list.Count < max && guard++ < 200)
            {
                int p;
                lock (rnd) p = rnd.Next(20000, 60000);
                if (!list.Contains(p)) list.Add(p);
            }
            return list;
        }

        public static async Task<IgdMapResult> MapAsync(IgdClient c, IgdService svc, IPAddress internalIp, int internalPort, int preferredExternal, string description,
            int leaseSeconds, bool mapUdp, int maxAttempts, Random rnd, Action<string> log, CancellationToken ct)
        {
            if (log == null) log = s => { };
            if (rnd == null) rnd = new Random();
            int preferred = preferredExternal > 0 ? preferredExternal : internalPort;
            var candidates = Candidates(preferred, Math.Max(1, maxAttempts), rnd);
            UpnpException last = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int ext = candidates[i];
                int lease = leaseSeconds;
                try
                {
                    lease = await AddWithLeaseFallbackAsync(c, svc, "TCP", ext, internalIp, internalPort, description, lease, log, ct).ConfigureAwait(false);
                }
                catch (UpnpException ex) when (ex.Kind == UpnpErrorKind.Soap)
                {
                    last = ex;
                    if (ex.ErrorCode == UpnpException.ConflictInMappingEntry || ex.ErrorCode == UpnpException.ConflictWithOtherMechanisms)
                    {
                        log("external port " + ext + " busy on the router (" + ex.ErrorCode + "): trying another");
                        continue;
                    }
                    if (ex.ErrorCode == UpnpException.SamePortValuesRequired && ext != internalPort)
                    {
                        // The router demands external == internal: only that port will do.
                        log("the router requires external port = internal (724)");
                        if (candidates.IndexOf(internalPort) <= i) candidates.Insert(i + 1, internalPort);
                        continue;
                    }
                    throw;
                }

                try
                {
                    // Verification: the router must show the mapping as ours.
                    bool unverified = false;
                    PortMappingEntry entry = null;
                    try { entry = await c.GetSpecificPortMappingEntryAsync(svc, "TCP", ext, ct).ConfigureAwait(false); unverified = entry == null; }
                    catch (UpnpException ex) when (ex.Kind != UpnpErrorKind.Soap || ex.ErrorCode != UpnpException.NoSuchEntry) { log("could not verify the mapping: " + ex.Message); }
                    if (entry != null && (entry.InternalPort != internalPort || !string.Equals(entry.InternalClient.Trim(), internalIp.ToString(), StringComparison.Ordinal)) && entry.InternalClient.Length > 0)
                    {
                        log("the router shows port " + ext + " pointing to " + entry.InternalClient + ":" + entry.InternalPort + ", not at us: trying another");
                        last = new UpnpException(UpnpErrorKind.Protocol, "the mapping of port " + ext + " belongs to another machine");
                        continue;
                    }
                    if (unverified)
                    {
                        // Some routers accept the mapping and still answer 714 when queried; it is taken as good (discarding it would lose a mapping that may work) but noted.
                        log("the router said OK but GetSpecificPortMappingEntry does not show the mapping of port " + ext + " (714): accepted without verification");
                    }

                    bool udp = false;
                    if (mapUdp)
                    {
                        try
                        {
                            await AddWithLeaseFallbackAsync(c, svc, "UDP", ext, internalIp, internalPort, description, lease, log, ct).ConfigureAwait(false);
                            udp = true;
                        }
                        catch (UpnpException ex) { log("UDP mapping of port " + ext + " unavailable (" + ex.Message + "); continuing with TCP"); }
                    }
                    return new IgdMapResult { ExternalPort = ext, LeaseSeconds = lease, UdpMapped = udp, Unverified = unverified };
                }
                catch (OperationCanceledException)
                {
                    // Cancelled with the mapping already created: it is deleted so as not to leave it open until the lease expires.
                    await CleanupAsync(c, svc, ext, log).ConfigureAwait(false);
                    throw;
                }
            }
            throw last ?? new UpnpException(UpnpErrorKind.Protocol, "no free external port");
        }

        /// <summary>
        /// AddPortMapping with the requested lease; on 725 (permanent only) or 402/501 with a lease other than 0, once more with lease 0.
        /// Returns the lease granted.
        /// </summary>
        public static async Task<int> AddWithLeaseFallbackAsync(IgdClient c, IgdService svc, string protocol, int ext, IPAddress internalIp, int internalPort, string description,
            int lease, Action<string> log, CancellationToken ct)
        {
            try
            {
                await c.AddPortMappingAsync(svc, protocol, ext, internalIp, internalPort, description, lease, ct).ConfigureAwait(false);
                return lease;
            }
            catch (UpnpException ex) when (lease != 0 && ex.Kind == UpnpErrorKind.Soap
                && (ex.ErrorCode == UpnpException.OnlyPermanentLeasesSupported || ex.ErrorCode == UpnpException.InvalidArgs || ex.ErrorCode == UpnpException.ActionFailed))
            {
                if (log != null) log("the router does not support lease (" + ex.Message + "): retrying with lease 0 (permanent; it will be deleted on stop)");
                await c.AddPortMappingAsync(svc, protocol, ext, internalIp, internalPort, description, 0, ct).ConfigureAwait(false);
                return 0;
            }
        }

        private static async Task CleanupAsync(IgdClient c, IgdService svc, int ext, Action<string> log)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try { await RemoveAsync(c, svc, ext, true, log, cts.Token).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }

        /// <summary>Deletes TCP and (if there was one) UDP. Best effort: returns true if TCP was deleted or no longer existed.</summary>
        public static async Task<bool> RemoveAsync(IgdClient c, IgdService svc, int externalPort, bool udp, Action<string> log, CancellationToken ct)
        {
            bool ok = true;
            try { await c.DeletePortMappingAsync(svc, "TCP", externalPort, ct).ConfigureAwait(false); }
            catch (UpnpException ex) { ok = false; if (log != null) log("could not delete the TCP mapping " + externalPort + ": " + ex.Message); }
            if (udp)
            {
                try { await c.DeletePortMappingAsync(svc, "UDP", externalPort, ct).ConfigureAwait(false); }
                catch (UpnpException ex) { if (log != null) log("could not delete the UDP mapping " + externalPort + ": " + ex.Message); }
            }
            return ok;
        }
    }
}
