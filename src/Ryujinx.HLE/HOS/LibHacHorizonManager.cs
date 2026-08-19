using LibHac;
using LibHac.Bcat;
using LibHac.Common;
using LibHac.FsSrv.Impl;
using LibHac.Loader;
using LibHac.Ncm;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS.Services.Arp;
using System;

namespace Ryujinx.HLE.HOS
{
    public class LibHacHorizonManager
    {
        private LibHac.Horizon Server { get; set; }

        public HorizonClient RyujinxClient { get; private set; }
        public HorizonClient ApplicationClient { get; private set; }
        public HorizonClient AccountClient { get; private set; }
        public HorizonClient AmClient { get; private set; }
        public HorizonClient BcatClient { get; private set; }
        public HorizonClient FsClient { get; private set; }
        public HorizonClient NsClient { get; private set; }
        public HorizonClient PmClient { get; private set; }
        public HorizonClient SdbClient { get; private set; }

        private SharedRef<LibHacIReader> _arpIReader;
        internal LibHacIReader ArpIReader => _arpIReader.Get;

        public LibHacHorizonManager()
        {
            InitializeServer();
        }

        private void InitializeServer()
        {
            Server = new LibHac.Horizon(new HorizonConfiguration());

            RyujinxClient = Server.CreatePrivilegedHorizonClient();
        }

        public void InitializeArpServer()
        {
            _arpIReader.Reset(new LibHacIReader());
            RyujinxClient.Sm.RegisterService(new LibHacArpServiceObject(ref _arpIReader), "arp:r").ThrowIfFailure();
        }

        public void InitializeBcatServer()
        {
            BcatClient = Server.CreateHorizonClient(new ProgramLocation(SystemProgramId.Bcat, StorageId.BuiltInSystem), BcatFsPermissions);

            _ = new BcatServer(BcatClient);
        }

        public void InitializeFsServer(VirtualFileSystem virtualFileSystem)
        {
            virtualFileSystem.InitializeFsServer(Server, out HorizonClient fsClient);

            FsClient = fsClient;
        }

        public void InitializeSystemClients()
        {
#pragma warning disable IDE0055 // Disable formatting
            PmClient      = Server.CreatePrivilegedHorizonClient();
            AccountClient = Server.CreateHorizonClient(new ProgramLocation(SystemProgramId.Account, StorageId.BuiltInSystem), AccountFsPermissions);
            AmClient      = Server.CreateHorizonClient(new ProgramLocation(SystemProgramId.Am,      StorageId.BuiltInSystem), AmFsPermissions);
            NsClient      = Server.CreateHorizonClient(new ProgramLocation(SystemProgramId.Ns,      StorageId.BuiltInSystem), NsFsPermissions);
            SdbClient     = Server.CreateHorizonClient(new ProgramLocation(SystemProgramId.Sdb,     StorageId.BuiltInSystem), SdbFacData, SdbFacDescriptor);
#pragma warning restore IDE0055
        }

        public void InitializeApplicationClient(ProgramId programId, in Npdm npdm)
        {
            ApplicationClient = Server.CreateHorizonClient(new ProgramLocation(programId, StorageId.BuiltInUser), npdm.FsAccessControlData, npdm.FsAccessControlDescriptor);
        }

        private static AccessControlBits.Bits AccountFsPermissions => AccessControlBits.Bits.SystemSaveData |
                                                                      AccessControlBits.Bits.GameCard |
                                                                      AccessControlBits.Bits.SaveDataMeta |
                                                                      AccessControlBits.Bits.GetRightsId;

        private static AccessControlBits.Bits AmFsPermissions => AccessControlBits.Bits.SaveDataManagement |
                                                                 AccessControlBits.Bits.CreateSaveData |
                                                                 AccessControlBits.Bits.SystemData;
        // [Nextendo] Le client BCAT n'avait que SystemSaveData, ce qui suffit a ses propres donnees
        // systeme mais PAS a ouvrir le cache de livraison d'une APPLICATION. Splatoon 3, qui demande
        // le sien au demarrage, se faisait refuser : 2002-6400 (PermissionDenied) cote fichiers,
        // remonte au jeu tel quel, boucle de reessais puis erreur a l'ecran. On ajoute donc le droit
        // sur les sauvegardes BCAT, et de quoi la creer si elle n'existe pas encore — c'est ce que
        // fait une console au premier lancement d'un jeu qui utilise BCAT.
        // [Nextendo] Ces droits restent ceux d'origine, volontairement. Ils avaient ete elargis en
        // cherchant la cause du refus d'ouverture du cache BCAT ; le banc de test a montre que
        // l'elargissement n'y changeait RIEN. Le vrai defaut etait le proprietaire de la sauvegarde
        // (voir VirtualFileSystem.FixExtraData), et une fois corrige, l'ouverture reussit avec ce seul
        // bit. Donner plus a un service systeme sans necessite n'a pas lieu d'etre.
        private static AccessControlBits.Bits BcatFsPermissions => AccessControlBits.Bits.SystemSaveData;

        private static AccessControlBits.Bits NsFsPermissions => AccessControlBits.Bits.ApplicationInfo |
                                                                 AccessControlBits.Bits.SystemSaveData |
                                                                 AccessControlBits.Bits.GameCard |
                                                                 AccessControlBits.Bits.SaveDataManagement |
                                                                 AccessControlBits.Bits.ContentManager |
                                                                 AccessControlBits.Bits.ImageManager |
                                                                 AccessControlBits.Bits.SystemSaveDataManagement |
                                                                 AccessControlBits.Bits.SystemUpdate |
                                                                 AccessControlBits.Bits.SdCard |
                                                                 AccessControlBits.Bits.FormatSdCard |
                                                                 AccessControlBits.Bits.GetRightsId |
                                                                 AccessControlBits.Bits.RegisterProgramIndexMapInfo |
                                                                 AccessControlBits.Bits.MoveCacheStorage;

        // Sdb has save data access control info so we can't store just its access control bits
        private static ReadOnlySpan<byte> SdbFacData =>
        [
            0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
            0x03, 0x03, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x01
        ];

        private static ReadOnlySpan<byte> SdbFacDescriptor =>
        [
            0x01, 0x00, 0x02, 0x00, 0x08, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x09, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01
        ];
    }
}
