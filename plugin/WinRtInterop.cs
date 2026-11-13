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
        public static Task<T> AsTask<T>(this IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            // Contrat WinRT : si l'opération est déjà terminée, Completed est appelé immédiatement.
            op.Completed = (o, status) =>
            {
                if (status == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                else if (status == AsyncStatus.Error) tcs.TrySetException(o.ErrorCode);
                else tcs.TrySetCanceled();
            };
            return tcs.Task;
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
    }
}
