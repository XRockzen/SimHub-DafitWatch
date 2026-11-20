using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace User.DafitWatch
{
    /// <summary>
    /// Petits adaptateurs WinRT -> .NET.
    /// Les extensions officielles (System.Runtime.WindowsRuntime : AsTask, AsBuffer, ToArray)
    /// exigent le SDK Windows pour compiler ; on les réécrit ici pour s'en passer.
    /// </summary>
    internal static class WinRtInterop
    {
        /// <summary>
        /// Attend une opération WinRT. La suite du code reprend sur le pool de threads
        /// (jamais sur le thread de rappel du pilote Bluetooth, qui peut se bloquer).
        /// </summary>
        public static Task<T> AsTask<T>(this IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Contrat WinRT : si l'opération est déjà terminée, Completed est appelé immédiatement.
            op.Completed = (o, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                    else if (status == AsyncStatus.Error) tcs.TrySetException(o.ErrorCode);
                    else tcs.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };
            return tcs.Task;
        }

        /// <summary>Comme AsTask, avec un délai maximal : l'opération est annulée au-delà.</summary>
        public static async Task<T> AsTask<T>(this IAsyncOperation<T> op, int timeoutMs, CancellationToken ct)
        {
            var task = op.AsTask();
            var delay = Task.Delay(timeoutMs, ct);
            if (await Task.WhenAny(task, delay).ConfigureAwait(false) != task)
            {
                try { op.Cancel(); } catch { }
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("la montre ne répond pas");
            }
            return await task.ConfigureAwait(false);
        }

        public static IBuffer ToBuffer(byte[] data)
        {
            var writer = new DataWriter();
            writer.WriteBytes(data);
            return writer.DetachBuffer();
        }

        public static byte[] ToBytes(IBuffer buffer)
        {
            var bytes = new byte[buffer.Length];
            if (bytes.Length > 0)
                DataReader.FromBuffer(buffer).ReadBytes(bytes);
            return bytes;
        }

        /// <summary>Message lisible pour les erreurs Bluetooth courantes de Windows.</summary>
        public static string Describe(Exception ex)
        {
            var agg = ex as AggregateException;
            if (agg != null && agg.InnerException != null) ex = agg.InnerException;
            switch ((uint)ex.HResult)
            {
                case 0x80000013: return "liaison perdue (objet fermé)";
                case 0x80004004: return "liaison perdue (opération abandonnée)";
                case 0x8000000E: return "liaison perdue (état inattendu)";
                case 0x80070005: return "accès refusé par Windows";
                case 0x8007048F: return "montre déconnectée";
                case 0x800710DF: return "Bluetooth désactivé";
            }
            return ex.Message;
        }
    }
}
