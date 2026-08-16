using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Types;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Sockets.Bsd.Impl
{
    class EventFileDescriptorPollManager : IPollManager
    {
        private static EventFileDescriptorPollManager _instance;

        public static EventFileDescriptorPollManager Instance
        {
            get
            {
                _instance ??= new EventFileDescriptorPollManager();

                return _instance;
            }
        }

        public bool IsCompatible(PollEvent evnt)
        {
            return evnt.FileDescriptor is EventFileDescriptor;
        }

        public LinuxError Poll(List<PollEvent> events, int timeoutMilliseconds, out int updatedCount)
        {
            updatedCount = 0;

            List<ManualResetEvent> waiters = [];

            for (int i = 0; i < events.Count; i++)
            {
                PollEvent evnt = events[i];

                EventFileDescriptor socket = (EventFileDescriptor)evnt.FileDescriptor;

                bool isValidEvent = false;

                if (evnt.Data.InputEvents.HasFlag(PollEventTypeMask.Input) ||
                    evnt.Data.InputEvents.HasFlag(PollEventTypeMask.UrgentInput))
                {
                    waiters.Add(socket.ReadEvent);

                    isValidEvent = true;
                }

                if (evnt.Data.InputEvents.HasFlag(PollEventTypeMask.Output))
                {
                    waiters.Add(socket.WriteEvent);

                    isValidEvent = true;
                }

                // [Nextendo] Un sondage qui ne demande AUCUN interet lecture/ecriture (InputEvents == 0) est
                // VALIDE en POSIX : il attend le delai imparti et ne rapporte que POLLERR/POLLHUP. gRPC sonde
                // son eventfd de reveil exactement ainsi pendant qu'il attend la fin d'un connect asynchrone.
                // Le refuser avec EINVAL cassait sa boucle de sondage : il fermait le descripteur, repartait
                // en boucle d'EBADF, et la session ne s'etablissait jamais. On l'accepte donc et on attend
                // l'evenement de lecture, pour qu'une ecriture sur l'eventfd reveille quand meme le sondage,
                // conformement a l'intention de gRPC ; aucun drapeau de sortie n'est pose.
                if (evnt.Data.InputEvents == 0)
                {
                    waiters.Add(socket.ReadEvent);

                    isValidEvent = true;
                }

                if (!isValidEvent)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Unsupported Poll input event type: {evnt.Data.InputEvents}");

                    return LinuxError.EINVAL;
                }
            }

            int index = WaitHandle.WaitAny(waiters.ToArray(), timeoutMilliseconds);

            if (index != WaitHandle.WaitTimeout)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    PollEventTypeMask outputEvents = 0;

                    PollEvent evnt = events[i];

                    EventFileDescriptor socket = (EventFileDescriptor)evnt.FileDescriptor;

                    if (socket.ReadEvent.WaitOne(0))
                    {
                        // [Nextendo] Signaler la lisibilite pour un sondage Input ordinaire ET pour un
                        // sondage a InputEvents == 0 : gRPC sonde son eventfd de reveil de cette seconde
                        // maniere et attend un retour « pret » des qu'il ecrit dedans. Sans cette ligne, le
                        // sondage restait differe indefiniment (jamais reveille) et la couche NPLN qui en
                        // depend ne demarrait pas. Rapporter Input quand l'eventfd est lisible laisse le
                        // reveil de gRPC aboutir.
                        if (evnt.Data.InputEvents.HasFlag(PollEventTypeMask.Input) || evnt.Data.InputEvents == 0)
                        {
                            outputEvents |= PollEventTypeMask.Input;
                        }

                        if (evnt.Data.InputEvents.HasFlag(PollEventTypeMask.UrgentInput))
                        {
                            outputEvents |= PollEventTypeMask.UrgentInput;
                        }
                    }

                    if ((evnt.Data.InputEvents.HasFlag(PollEventTypeMask.Output))
                        && socket.WriteEvent.WaitOne(0))
                    {
                        outputEvents |= PollEventTypeMask.Output;
                    }

                    if (outputEvents != 0)
                    {
                        evnt.Data.OutputEvents = outputEvents;

                        updatedCount++;
                    }
                }
            }
            else
            {
                return LinuxError.ETIMEDOUT;
            }

            return LinuxError.SUCCESS;
        }

        public LinuxError Select(List<PollEvent> events, int timeout, out int updatedCount)
        {
            // TODO: Implement Select for event file descriptors
            updatedCount = 0;

            return LinuxError.EOPNOTSUPP;
        }
    }
}
