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

                // [Nextendo] A poll requesting NO read/write interest (InputEvents == 0) is VALID in POSIX:
                // it blocks for the timeout and reports only POLLERR/POLLHUP. grpc-core polls its wakeup
                // eventfd exactly this way while waiting for an async connect to complete. Rejecting it with
                // EINVAL broke grpc's poll loop -> it closed the fd (EBADF loop) -> S3 NPLN never connected
                // (stuck on the online ink-loading screen). Treat it as valid and wait on the read event so a
                // wakeup (eventfd write) still unblocks the poll, matching grpc's intent; no output flags set.
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
                        // [Nextendo] Report readability for a plain Input poll AND for an events==0 poll:
                        // grpc-core polls its wakeup eventfd with InputEvents==0 and expects the poll to
                        // return "ready" once it writes that eventfd. Without reporting it here, the grpc
                        // poll deferred forever (never woke) -> S3's friends/presence flow (plaza gate) never
                        // ran. Surfacing Input when the eventfd is readable lets grpc's wakeup complete.
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
