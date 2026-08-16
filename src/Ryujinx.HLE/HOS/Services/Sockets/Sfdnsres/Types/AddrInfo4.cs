using Ryujinx.Common.Memory;
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Sockets.Sfdnsres.Types
{
    // [Nextendo] sockaddr compacte rendue par getaddrinfo. Le seul vrai defaut etait sin_len : il
    // valait sizeof(Array4) = 4 au lieu de la taille complete de la sockaddr (16). Avec sin_len = 4,
    // le resolveur invite ne lisait que 4 octets et n'en tirait jamais l'adresse : il construisait sa
    // sockaddr de connexion avec une adresse nulle et tentait un connect vers 0.0.0.0 (« port ok,
    // adresse 0 » dans la trace).
    //
    // Mesure faite sur la sockaddr de connexion reellement construite par grpc : une fois sin_len = 16,
    // grpc lit bien l'adresse et la projette en IPv4-mapped IPv6 pour sa socket double pile
    //     00 1C <port> <flowinfo> ::ffff:<adresse> <scope>
    // et il relit nos sin_port / sin_addr en ordre HOTE (petit-boutiste) avant d'appliquer htons/htonl,
    // c'est-a-dire qu'il les inverse une fois. Les octets sur le fil doivent donc rester en ordre HOTE,
    // PAS en ordre reseau. Le code d'origine produisait deja de l'ordre hote (HostToNetworkOrder dans le
    // constructeur puis un second echange dans ToNetworkOrder) : ne pas « corriger » cela en ordre
    // reseau, l'essai a fait connecter grpc vers l'adresse aux octets inverses et le jeu a plante.
    // Seul sin_len devait etre repare.
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 0x10)]
    struct AddrInfo4
    {
        public byte Length;
        public byte Family;
        public short Port;
        public Array4<byte> Address;
        public Array8<byte> Padding;

        public AddrInfo4(IPAddress address, short port)
        {
            Length = (byte)Unsafe.SizeOf<AddrInfo4>(); // 16 (valait sizeof(Array4) = 4 : le defaut)
            Family = (byte)AddressFamily.InterNetwork;
            Port = IPAddress.HostToNetworkOrder(port);
            Address = new Array4<byte>();

            address.TryWriteBytes(Address.AsSpan(), out _);
        }

        public void ToNetworkOrder()
        {
            Port = IPAddress.HostToNetworkOrder(Port);

            RawIpv4AddressNetworkEndianSwap(Address.AsSpan());
        }

        public void ToHostOrder()
        {
            Port = IPAddress.NetworkToHostOrder(Port);

            RawIpv4AddressNetworkEndianSwap(Address.AsSpan());
        }

        public static void RawIpv4AddressNetworkEndianSwap(Span<byte> address)
        {
            if (BitConverter.IsLittleEndian)
            {
                address.Reverse();
            }
        }
    }
}
