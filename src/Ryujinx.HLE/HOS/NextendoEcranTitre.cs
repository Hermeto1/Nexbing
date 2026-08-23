using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.RomFs;
using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ryujinx.HLE.HOS
{
    /// <summary>
    /// Ecran-titre de Splatoon 3 : afficher le logo de l'evenement en cours.
    ///
    /// CE QUE C'EST, ET CE QUE CE N'EST PAS. Les cinq logos — le normal, Frosty Fest, Spring Fest,
    /// Summer Nights et Grand Festival — sont DEJA dans le fichier du joueur. Lequel s'affiche est
    /// decide par le code du jeu, et rien dans les donnees ne permet d'atteindre ce choix. On ne
    /// remplace donc aucune image : on PERMUTE deux textures que Nintendo a livrees.
    ///
    /// Les cinq font exactement 1 572 864 octets, en ASTC 4x4, 1530x925. L'echange est donc neutre
    /// au bit pres : le fichier decompresse garde sa taille, la table des tailles du jeu reste
    /// valide, aucun nom ni decalage ne bouge. Rien n'est ajoute au jeu, aucun comportement de
    /// partie n'est modifie — c'est pour cela que ceci n'est pas un mod au sens ou ModsInterdits
    /// les refuse, et que ce chemin vit en dehors du chargeur de mods.
    ///
    /// RIEN NE SORT DE LA MACHINE DU JOUEUR. La permutation se fait en memoire, a partir de SON
    /// dump. Aucun fichier patche n'est distribue : il contiendrait les textures de Nintendo.
    ///
    /// LE DEFAUT EST DE NE RIEN FAIRE. Champ absent, vide, « default », valeur inconnue, fichier
    /// introuvable, geometrie inattendue : on rend le romfs d'origine, intact. Un joueur ne doit
    /// jamais se retrouver avec un ecran-titre casse parce que le serveur a repondu autre chose que
    /// prevu.
    /// </summary>
    public static class NextendoEcranTitre
    {
        private const ulong Splatoon3 = 0x0100C2500FC20000;
        private const string CheminLayout = "/Layout/Plz_Title_00.Nin_NX_NVN.blarc.zs";

        /// <summary>La texture affichee hors evenement : c'est elle qu'on remplace.</summary>
        private const string LogoNormal = "Logo_01";

        /// <summary>Taille exacte d'un bloc de logo. Toute autre valeur fait renoncer.</summary>
        private const int OctetsAttendus = 1572864;

        private static readonly Dictionary<string, string> _variantes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["frosty"] = "Logo_03",
            ["spring"] = "Logo_04",
            ["summer"] = "Logo_05",
            ["grand"] = "Logo_06",
        };

        /// <summary>
        /// Le logo demande, renseigne par la posture de fete du serveur. Volontairement accroche a
        /// elle : pose a cote, il survivrait a la fin de la fete et le logo resterait coince.
        /// </summary>
        public static string Demande { get; set; }

        private static string LogoDemande()
        {
            string cle = Demande;

            // Interrupteur de developpement, pour pouvoir essayer une variante avant que le serveur
            // ne la serve. Il ne prend la main que si le serveur n'a rien dit.
            if (string.IsNullOrWhiteSpace(cle))
            {
                cle = Environment.GetEnvironmentVariable("NEXTENDO_LOGO_TITRE");
            }

            if (string.IsNullOrWhiteSpace(cle) || cle.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return _variantes.TryGetValue(cle.Trim(), out string logo) ? logo : null;
        }

        public static IStorage Appliquer(ulong applicationId, IStorage baseStorage)
        {
            if (applicationId != Splatoon3 || baseStorage == null)
            {
                return baseStorage;
            }

            string logo = LogoDemande();
            if (logo == null)
            {
                return baseStorage;
            }

            try
            {
                return Permuter(baseStorage, logo);
            }
            catch (Exception e)
            {
                // Un ecran-titre n'a jamais valu qu'on empeche un joueur de jouer.
                Logger.Warning?.Print(LogClass.ModLoader,
                    $"[Nextendo] Ecran-titre : permutation abandonnee ({e.GetType().Name}: {e.Message}) — romfs d'origine conserve");

                return baseStorage;
            }
        }

        private static IStorage Permuter(IStorage baseStorage, string logo)
        {
            RomFsFileSystem baseRom = new(baseStorage);

            byte[] compresse = LireFichier(baseRom, CheminLayout);
            if (compresse == null)
            {
                Logger.Info?.Print(LogClass.ModLoader,
                    $"[Nextendo] Ecran-titre : {CheminLayout} absent du jeu — rien a faire");

                return baseStorage;
            }

            byte[] clair;
            using (ZstdSharp.Decompressor decompresseur = new())
            {
                clair = decompresseur.Unwrap(compresse).ToArray();
            }

            int tailleAvant = clair.Length;
            if (!PermuterTextures(clair, LogoNormal, logo, out string pourquoi))
            {
                Logger.Info?.Print(LogClass.ModLoader,
                    $"[Nextendo] Ecran-titre : {pourquoi} — romfs d'origine conserve");

                return baseStorage;
            }

            if (clair.Length != tailleAvant)
            {
                // Impossible par construction, mais la table des tailles du jeu en depend.
                throw new InvalidOperationException("la taille decompressee a change");
            }

            byte[] recompresse;
            using (ZstdSharp.Compressor compresseur = new(15))
            {
                recompresse = compresseur.Wrap(clair).ToArray();
            }

            RomFsBuilder builder = new();
            builder.AddFile(CheminLayout, new StorageFile(new MemoryStorage(recompresse), OpenMode.Read));

            foreach (DirectoryEntryEx entree in baseRom.EnumerateEntries()
                         .Where(f => f.Type == DirectoryEntryType.File && f.FullPath != CheminLayout)
                         .OrderBy(f => f.FullPath, StringComparer.Ordinal))
            {
                using UniqueRef<IFile> fichier = new();

                baseRom.OpenFile(ref fichier.Ref, entree.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                builder.AddFile(entree.FullPath, fichier.Release());
            }

            Logger.Info?.Print(LogClass.ModLoader,
                $"[Nextendo] Ecran-titre : {LogoNormal} <-> {logo} ({tailleAvant} o inchanges)");

            return builder.Build();
        }

        private static byte[] LireFichier(RomFsFileSystem fs, string chemin)
        {
            using UniqueRef<IFile> fichier = new();

            if (fs.OpenFile(ref fichier.Ref, chemin.ToU8Span(), OpenMode.Read).IsFailure())
            {
                return null;
            }

            fichier.Get.GetSize(out long taille).ThrowIfFailure();

            byte[] donnees = new byte[taille];
            fichier.Get.Read(out long lu, 0, donnees, ReadOption.None).ThrowIfFailure();

            return lu == taille ? donnees : null;
        }

        // ---- BNTX : juste ce qu'il faut pour situer deux textures et echanger leurs octets ----

        private readonly struct Texture
        {
            public readonly int Debut;
            public readonly int Octets;
            public readonly uint Format;
            public readonly uint Largeur;
            public readonly uint Hauteur;

            public Texture(int debut, int octets, uint format, uint largeur, uint hauteur)
            {
                Debut = debut;
                Octets = octets;
                Format = format;
                Largeur = largeur;
                Hauteur = hauteur;
            }
        }

        private static bool PermuterTextures(byte[] clair, string nomA, string nomB, out string pourquoi)
        {
            Dictionary<string, Texture> textures = Inventorier(clair);

            if (!textures.TryGetValue(nomA, out Texture a) || !textures.TryGetValue(nomB, out Texture b))
            {
                pourquoi = $"{nomA} ou {nomB} introuvable dans le fichier du joueur";

                return false;
            }

            if (a.Octets != OctetsAttendus || b.Octets != OctetsAttendus)
            {
                pourquoi = $"tailles inattendues ({a.Octets} et {b.Octets}, attendu {OctetsAttendus})";

                return false;
            }

            if (a.Largeur != b.Largeur || a.Hauteur != b.Hauteur || a.Format != b.Format)
            {
                pourquoi = "geometries ou formats differents";

                return false;
            }

            byte[] tampon = new byte[OctetsAttendus];
            Buffer.BlockCopy(clair, a.Debut, tampon, 0, OctetsAttendus);
            Buffer.BlockCopy(clair, b.Debut, clair, a.Debut, OctetsAttendus);
            Buffer.BlockCopy(tampon, 0, clair, b.Debut, OctetsAttendus);

            pourquoi = null;

            return true;
        }

        private static Dictionary<string, Texture> Inventorier(byte[] donnees)
        {
            Dictionary<string, Texture> out_ = new(StringComparer.Ordinal);

            int bntx = Trouver(donnees, "BNTX"u8);
            if (bntx < 0)
            {
                return out_;
            }

            int nx = bntx + 0x20;
            if (!Correspond(donnees, nx, "NX  "u8))
            {
                return out_;
            }

            uint nombre = U32(donnees, nx + 4);
            long table = bntx + (long)U64(donnees, nx + 8);

            for (uint i = 0; i < nombre; i++)
            {
                long p = bntx + (long)U64(donnees, (int)(table + i * 8));
                if (p < 0 || p + 0x78 > donnees.Length || !Correspond(donnees, (int)p, "BRTI"u8))
                {
                    continue;
                }

                long nomPtr = bntx + (long)U64(donnees, (int)(p + 0x60));
                if (nomPtr < 0 || nomPtr + 2 > donnees.Length)
                {
                    continue;
                }

                int longueur = BinaryPrimitives.ReadUInt16LittleEndian(donnees.AsSpan((int)nomPtr, 2));
                if (longueur <= 0 || nomPtr + 2 + longueur > donnees.Length)
                {
                    continue;
                }

                string nom = Encoding.ASCII.GetString(donnees, (int)nomPtr + 2, longueur);

                // Les noms portent un suffixe de canal : « Logo_01^w ».
                int chapeau = nom.IndexOf('^');
                if (chapeau > 0)
                {
                    nom = nom[..chapeau];
                }

                long indirection = (long)U64(donnees, (int)(p + 0x70));
                if (indirection == 0 || bntx + indirection + 8 > donnees.Length)
                {
                    continue;
                }

                long debut = bntx + (long)U64(donnees, (int)(bntx + indirection));
                int octets = (int)U32(donnees, (int)(p + 0x50));

                if (debut < 0 || octets <= 0 || debut + octets > donnees.Length)
                {
                    continue;
                }

                out_[nom] = new Texture((int)debut, octets,
                    U32(donnees, (int)(p + 0x1C)), U32(donnees, (int)(p + 0x24)), U32(donnees, (int)(p + 0x28)));
            }

            return out_;
        }

        private static int Trouver(byte[] donnees, ReadOnlySpan<byte> motif)
        {
            return donnees.AsSpan().IndexOf(motif);
        }

        private static bool Correspond(byte[] donnees, int position, ReadOnlySpan<byte> motif)
        {
            return position >= 0
                && position + motif.Length <= donnees.Length
                && donnees.AsSpan(position, motif.Length).SequenceEqual(motif);
        }

        private static uint U32(byte[] d, int p) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));

        private static ulong U64(byte[] d, int p) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p, 8));
    }
}
