using Ryujinx.Common.Logging;
using Ryujinx.HLE.Loaders.Mods;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.HLE.HOS
{
    /// <summary>
    /// Les correctifs que Splatoon 3 exige pour parler a nos serveurs, INTEGRES au binaire.
    ///
    /// Pourquoi ils sont ici et plus sur le disque :
    ///
    /// 1. PLUG AND PLAY. Jusqu'ici il fallait envoyer aux joueurs un dossier
    ///    portable/sdcard/atmosphere/exefs_patches/ a recopier a la main. Sans lui, le TLS du jeu
    ///    s'arretait au ClientHello et rien ne se connectait. Pour un test OUVERT, c'est une marche
    ///    trop haute et une source d'erreurs sans fin.
    ///
    /// 2. COHERENCE AVEC L'INTERDICTION DES MODS. Ces correctifs sont des .ips, exactement la forme
    ///    que le chargeur de mods refuse desormais pour ce titre (voir ModLoader.ModsInterdits). Les
    ///    laisser sur le disque aurait oblige a percer une exception dans le blocage — et une
    ///    exception par NOM de dossier se contourne en renommant son mod. Integres, il n'y a plus
    ///    d'exception a percer : le disque est refuse en bloc, et ces octets-ci ne viennent pas du
    ///    disque.
    ///
    /// Ce sont les memes octets que les fichiers .ips utilises jusqu'ici (verifies par empreinte).
    /// Format IPS32 : "IPS32", puis des enregistrements {offset 4 o, taille 2 o, donnees}, puis "EEOF".
    /// </summary>
    internal static class NextendoS3Patches
    {
        // Contourne la verification de certificat : le jeu epingle les certificats de Nintendo et
        // refuse les notres. 19 octets, empreinte 219ee0cfea21fd4b…
        private static readonly byte[] _contournementCertificat =
        [
            0x49, 0x50, 0x53, 0x33, 0x32,             // "IPS32"
            0x00, 0x15, 0x7B, 0x20, 0x00, 0x04,       // offset 0x00157B20, 4 octets
            0x2A, 0x00, 0x80, 0x52,
            0x45, 0x45, 0x4F, 0x46,                   // "EEOF"
        ];

        // Corrige le nom d'hote presente au pair. 29 octets, empreinte aa8a453637092346…
        private static readonly byte[] _nomDePair =
        [
            0x49, 0x50, 0x53, 0x33, 0x32,             // "IPS32"
            0x00, 0x14, 0xE1, 0xB0, 0x00, 0x04,       // offset 0x0014E1B0, 4 octets
            0x1F, 0x20, 0x03, 0xD5,
            0x00, 0x14, 0xDD, 0x80, 0x00, 0x04,       // offset 0x0014DD80, 4 octets
            0xF4, 0x03, 0x1F, 0x2A,
            0x45, 0x45, 0x4F, 0x46,                   // "EEOF"
        ];

        /// <summary>
        /// Identifiant de build du NSO -> correctifs a lui appliquer.
        ///
        /// La cle est l'identifiant tel que le chargeur le calcule : hexadecimal MAJUSCULE, zeros de
        /// fin retires. Deux builds de Splatoon 3 sont couverts ; un identifiant inconnu ne recoit
        /// rien, exactement comme un .ips dont le nom ne correspondait a aucun programme.
        /// </summary>
        private static readonly Dictionary<string, byte[][]> _parIdentifiantDeBuild = new()
        {
            ["6830B3A12406CB4716FEC5ADDC35D3E2DC92D212"] = [_contournementCertificat, _nomDePair],
            ["726D2B882DD9EF10F4A9D73EED088740630FB6C8"] = [_contournementCertificat],
        };

        /// <summary>
        /// Verse dans <paramref name="cible"/> les correctifs connus pour cet identifiant de build.
        /// Rend le nombre de correctifs verses (0 si l'identifiant est inconnu).
        /// </summary>
        public static int Verser(string identifiantDeBuild, MemPatch cible)
        {
            if (string.IsNullOrEmpty(identifiantDeBuild) ||
                !_parIdentifiantDeBuild.TryGetValue(identifiantDeBuild, out byte[][] correctifs))
            {
                return 0;
            }

            foreach (byte[] octets in correctifs)
            {
                using MemoryStream flux = new(octets);
                using BinaryReader lecteur = new(flux);

                new IpsPatcher(lecteur).AddPatches(cible);
            }

            Logger.Info?.Print(LogClass.ModLoader,
                $"[Nextendo] Splatoon 3 : {correctifs.Length} correctif(s) integre(s) appliques (build {identifiantDeBuild})");

            return correctifs.Length;
        }
    }
}
