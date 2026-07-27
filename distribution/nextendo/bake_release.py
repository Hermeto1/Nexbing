#!/usr/bin/env python3
# [Nextendo] Release-time bake, driven by CI secrets/env — never committed with real values.
#
# The open-source tree resolves the Nextendo server addresses from environment variables and
# falls back to loopback (see DnsMitmResolver / NextendoNetworkCheck), and ships ReleaseInformation
# with %%RYUJINX_...%% placeholders. Official builds run this once, on a throwaway CI checkout, to
# bake the real addresses in and stamp the version. Addresses come from the NEXTENDO_SERVER_IP /
# NEXTENDO_NAT_IP secrets, never from this file.
#
# Usage: NEXTENDO_SERVER_IP=... NEXTENDO_NAT_IP=... bake_release.py <version> <git_hash>
import os, io, sys

server = os.environ["NEXTENDO_SERVER_IP"].strip()
nat = os.environ["NEXTENDO_NAT_IP"].strip()
version = sys.argv[1]
git_hash = sys.argv[2][:7]

def patch(rel, old, new):
    with io.open(rel, encoding="utf-8") as f:
        s = f.read()
    n = s.count(old)
    if n != 1:
        raise SystemExit(f"BAKE FAIL {rel}: expected exactly 1 match, found {n}")
    with io.open(rel, "w", encoding="utf-8", newline="") as f:
        f.write(s.replace(old, new))
    print(f"  baked {rel}")

# 1) DnsMitmResolver: real server IPs instead of the loopback fallback
patch("src/Ryujinx.HLE/HOS/Services/Sockets/Sfdnsres/Proxy/DnsMitmResolver.cs",
"""            string value = Environment.GetEnvironmentVariable(envVar);

            if (!IPAddress.TryParse(value, out IPAddress address))
            {
                Logger.Warning?.PrintMsg(LogClass.ServiceBsd, $"DnsMitmResolver: {envVar} not set, falling back to loopback — Nintendo hosts will resolve to 127.0.0.1");

                return IPAddress.Loopback;
            }

            return address;""",
f"""            string value = Environment.GetEnvironmentVariable(envVar);

            if (IPAddress.TryParse(value, out IPAddress address))
            {{
                return address;
            }}

            return IPAddress.Parse(envVar == "NEXTENDO_NAT_IP" ? "{nat}" : "{server}");""")

# 2) NextendoNetworkCheck: real nncs responders
patch("src/Ryujinx/Common/NextendoNetworkCheck.cs",
'''        private static readonly string Nncs1 = Environment.GetEnvironmentVariable("NEXTENDO_SERVER_IP") ?? "127.0.0.1";
        private static readonly string Nncs2 = Environment.GetEnvironmentVariable("NEXTENDO_NAT_IP") ?? "127.0.0.1";''',
f'''        private static readonly string Nncs1 = Environment.GetEnvironmentVariable("NEXTENDO_SERVER_IP") ?? "{server}";
        private static readonly string Nncs2 = Environment.GetEnvironmentVariable("NEXTENDO_NAT_IP") ?? "{nat}";''')

# 3) ReleaseInformation: stamp version/channel/config so IsValid is true (gates active)
patch("src/Ryujinx.Common/ReleaseInformation.cs",
'''        private const string BuildVersion = "%%RYUJINX_BUILD_VERSION%%";
        private const string BuildGitHash = "%%RYUJINX_BUILD_GIT_HASH%%";
        private const string ReleaseChannelName = "%%RYUJINX_TARGET_RELEASE_CHANNEL_NAME%%";
        private const string ConfigFileName = "%%RYUJINX_CONFIG_FILE_NAME%%";''',
f'''        private const string BuildVersion = "{version}";
        private const string BuildGitHash = "{git_hash}";
        private const string ReleaseChannelName = "release";
        private const string ConfigFileName = "Config.json";''')

print("BAKE OK")
