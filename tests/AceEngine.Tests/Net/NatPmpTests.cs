using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Net;
using Xunit;

namespace AceEngine.Tests.NetTests
{
    public class NatPmpTests
    {
        private static readonly IPAddress Gw = IPAddress.Parse("192.168.1.1");
        private static readonly IPAddress Me = IPAddress.Parse("192.168.1.50");

        private static NatPmpClient Client(NetLab lab, int attempts = 4, int initialMs = 40)
        {
            return new NatPmpClient(lab.Net, new NatPmpOptions { InitialTimeout = TimeSpan.FromMilliseconds(initialMs), MaxAttempts = attempts });
        }

        [Fact]
        public async Task External_address_request_matches_RFC6886()
        {
            using (var lab = new NetLab(null, true))
            {
                var ext = await Client(lab).GetExternalAddressAsync(Gw, Me, CancellationToken.None);
                Assert.Equal(IPAddress.Parse("203.0.113.7"), ext.Address);
                Assert.InRange(ext.Epoch, 1000u, 1100u);
                var req = lab.Pmp.Requests.First();
                Assert.Equal(new byte[] { 0, 0 }, req);              // version 0, opcode 0
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Mapping_request_layout_and_response(bool tcp)
        {
            using (var lab = new NetLab(null, true))
            {
                var m = await Client(lab).MapAsync(Gw, Me, tcp, 8621, 8621, 3600, CancellationToken.None);
                Assert.Equal(tcp, m.Tcp);
                Assert.Equal(8621, m.InternalPort);
                Assert.Equal(8621, m.ExternalPort);
                Assert.Equal(3600u, m.LifetimeSeconds);
                var req = lab.Pmp.Requests.First();
                Assert.Equal(12, req.Length);
                Assert.Equal(0, req[0]);
                Assert.Equal(tcp ? 2 : 1, req[1]);                    // opcode 1 = UDP, 2 = TCP
                Assert.Equal(new byte[] { 0, 0 }, new[] { req[2], req[3] });          // reserved
                Assert.Equal(8621, (req[4] << 8) | req[5]);
                Assert.Equal(8621, (req[6] << 8) | req[7]);
                Assert.Equal(3600, (req[8] << 24) | (req[9] << 16) | (req[10] << 8) | req[11]);
                Assert.True(lab.Pmp.Has(tcp, 8621));
            }
        }

        [Fact]
        public async Task Router_may_assign_another_external_port_and_a_shorter_lifetime()
        {
            using (var lab = new NetLab(null, true))
            {
                lab.Pmp.ExternalPortOffset = 5;
                lab.Pmp.LifetimeCap = 600;
                var m = await Client(lab).MapAsync(Gw, Me, true, 8621, 8621, 3600, CancellationToken.None);
                Assert.Equal(8626, m.ExternalPort);
                Assert.Equal(600u, m.LifetimeSeconds);
            }
        }

        [Fact]
        public async Task Delete_uses_lifetime_zero_and_port_zero()
        {
            using (var lab = new NetLab(null, true))
            {
                var c = Client(lab);
                await c.MapAsync(Gw, Me, true, 8621, 8621, 3600, CancellationToken.None);
                Assert.True(lab.Pmp.Has(true, 8621));
                await c.DeleteAsync(Gw, Me, true, 8621, CancellationToken.None);
                Assert.False(lab.Pmp.Has(true, 8621));
                var del = lab.Pmp.Requests.Last();
                Assert.Equal(0, (del[6] << 8) | del[7]);
                Assert.Equal(0, del[8] | del[9] | del[10] | del[11]);
            }
        }

        [Fact]
        public async Task Retransmits_with_doubling_timeouts_until_the_gateway_answers()
        {
            using (var lab = new NetLab(null, true))
            {
                lab.Pmp.DropFirst = 2;
                var sw = Stopwatch.StartNew();
                var ext = await Client(lab, attempts: 4, initialMs: 100).GetExternalAddressAsync(Gw, Me, CancellationToken.None);
                Assert.Equal(IPAddress.Parse("203.0.113.7"), ext.Address);
                Assert.Equal(3, lab.Pmp.RequestCount);                // 1 original + 2 retries
                Assert.InRange(sw.Elapsed.TotalMilliseconds, 280, 2500);    // 100 + 200 ms of waiting at minimum
            }
        }

        [Fact]
        public async Task No_answer_after_all_attempts_throws_NoResponse_with_the_rfc_schedule()
        {
            using (var lab = new NetLab(null, true))
            {
                lab.Pmp.Respond = false;
                var sw = Stopwatch.StartNew();
                var ex = await Assert.ThrowsAsync<NatPmpException>(() => Client(lab, attempts: 4, initialMs: 50).GetExternalAddressAsync(Gw, Me, CancellationToken.None));
                Assert.True(ex.NoResponse);
                Assert.Equal(4, lab.Pmp.RequestCount);
                Assert.InRange(sw.Elapsed.TotalMilliseconds, 700, 4000);      // 50 + 100 + 200 + 400 = 750 ms
                await Wait.UntilAsync(() => lab.Net.OpenSockets == 0, 1000);
                Assert.Equal(0, lab.Net.OpenSockets);
            }
        }

        [Fact]
        public async Task A_gateway_that_does_not_exist_times_out_too()
        {
            using (var lab = new NetLab(null, false))
            {
                var ex = await Assert.ThrowsAsync<NatPmpException>(() => Client(lab, 2, 30).GetExternalAddressAsync(Gw, Me, CancellationToken.None));
                Assert.True(ex.NoResponse);
            }
        }

        [Theory]
        [InlineData(1, "version")]
        [InlineData(2, "authorized")]
        [InlineData(3, "network")]
        [InlineData(4, "resources")]
        [InlineData(5, "unsupported")]
        public async Task Result_codes_become_exceptions(int code, string text)
        {
            using (var lab = new NetLab(null, true))
            {
                lab.Pmp.ForcedResult = code;
                var ex = await Assert.ThrowsAsync<NatPmpException>(() => Client(lab).MapAsync(Gw, Me, true, 8621, 8621, 3600, CancellationToken.None));
                Assert.Equal(code, ex.ResultCode);
                Assert.False(ex.NoResponse);
                Assert.Contains(text, ex.Message);
                var ex2 = await Assert.ThrowsAsync<NatPmpException>(() => Client(lab).GetExternalAddressAsync(Gw, Me, CancellationToken.None));
                Assert.Equal(code, ex2.ResultCode);
            }
        }

        [Fact]
        public async Task Replies_from_other_hosts_or_ports_are_ignored()
        {
            using (var lab = new NetLab(null, true))
            {
                lab.Pmp.SpoofSource = new IPEndPoint(IPAddress.Parse("192.168.1.99"), 5351);
                var ex = await Assert.ThrowsAsync<NatPmpException>(() => Client(lab, 2, 30).GetExternalAddressAsync(Gw, Me, CancellationToken.None));
                Assert.True(ex.NoResponse);
                lab.Pmp.SpoofSource = new IPEndPoint(Gw, 4000);
                var ex2 = await Assert.ThrowsAsync<NatPmpException>(() => Client(lab, 2, 30).GetExternalAddressAsync(Gw, Me, CancellationToken.None));
                Assert.True(ex2.NoResponse);
                lab.Pmp.SpoofSource = null;
                Assert.NotNull(await Client(lab).GetExternalAddressAsync(Gw, Me, CancellationToken.None));
            }
        }

        [Fact]
        public async Task Junk_malformed_and_mismatched_datagrams_do_not_confuse_the_client()
        {
            using (var lab = new NetLab(null, true))
            {
                foreach (var junk in new[]
                {
                    new byte[0], new byte[] { 1 }, new byte[] { 0, 128 }, new byte[12], new byte[16],
                    new byte[] { 1, 128, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4 },                  // version 1
                    new byte[] { 0, 129, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 },      // UDP opcode for a TCP request
                    new byte[] { 0, 130, 0, 0, 0, 0, 0, 1, 0, 0 },                         // short
                    new byte[200]
                })
                {
                    lab.Pmp.JunkBeforeReply = junk;
                    var m = await Client(lab).MapAsync(Gw, Me, true, 8621, 8621, 3600, CancellationToken.None);
                    Assert.Equal(8621, m.ExternalPort);
                }
                // a different internal port in the response is not valid either
                lab.Pmp.JunkBeforeReply = new byte[] { 0, 130, 0, 0, 0, 0, 0, 1, 0x22, 0x66, 0x22, 0x66, 0, 0, 0, 60 };
                var ok = await Client(lab).MapAsync(Gw, Me, true, 8621, 8621, 3600, CancellationToken.None);
                Assert.Equal(3600u, ok.LifetimeSeconds);
            }
        }

        [Fact]
        public async Task Cancellation_stops_waiting_and_closes_the_socket()
        {
            using (var lab = new NetLab(null, true))
            using (var cts = new CancellationTokenSource())
            {
                lab.Pmp.Respond = false;
                var t = Client(lab, 9, 2000).GetExternalAddressAsync(Gw, Me, cts.Token);
                await Task.Delay(100);
                var sw = Stopwatch.StartNew();
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
                await Wait.UntilAsync(() => lab.Net.OpenSockets == 0, 1000);
                Assert.Equal(0, lab.Net.OpenSockets);
            }
        }

        [Fact]
        public async Task Argument_validation()
        {
            using (var lab = new NetLab(null, true))
            {
                var c = Client(lab);
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.MapAsync(Gw, Me, true, 0, 0, 60, CancellationToken.None));
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.MapAsync(Gw, Me, true, 80, 70000, 60, CancellationToken.None));
                await Assert.ThrowsAsync<ArgumentException>(() => c.GetExternalAddressAsync(IPAddress.IPv6Loopback, Me, CancellationToken.None));
                Assert.Equal(0, lab.Pmp.RequestCount);
            }
        }
    }
}
