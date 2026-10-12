using System;
using System.IO;
using System.Runtime.InteropServices;
using PDFiumCore;

namespace OpenPdfStudio.Services.Pdf;

/// <summary>
/// Calls FPDF_SaveAsCopy directly. PDFiumCore's binding declares the callback's C <c>unsigned long</c> as a
/// 64-bit value, which is wrong on Windows, so the struct and callback are declared here with CULong.
/// Worker thread only.
/// </summary>
internal static class PdfSaver
{
    private const uint NoIncremental = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileWrite
    {
        public int Version;
        public IntPtr WriteBlock;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteBlockCallback(IntPtr self, IntPtr data, CULong size);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_SaveAsCopy(IntPtr document, ref FileWrite fileWrite, CULong flags);

    public static void Save(FpdfDocumentT document, Stream output)
    {
        Exception? writeError = null;
        var buffer = new byte[64 * 1024];

        WriteBlockCallback callback = (_, data, size) =>
        {
            try
            {
                var remaining = (long)size.Value;
                var offset = 0L;
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(remaining, buffer.Length);
                    Marshal.Copy(data + (nint)offset, buffer, 0, chunk);
                    output.Write(buffer, 0, chunk);
                    offset += chunk;
                    remaining -= chunk;
                }

                return 1;
            }
            catch (Exception ex)
            {
                writeError = ex;
                return 0;
            }
        };

        var fileWrite = new FileWrite
        {
            Version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(callback)
        };

        var ok = FPDF_SaveAsCopy(document.__Instance, ref fileWrite, new CULong(NoIncremental));
        GC.KeepAlive(callback);

        if (writeError is not null)
            throw new IOException("The PDF could not be written.", writeError);
        if (ok == 0)
            throw new InvalidOperationException("PDFium could not save the document.");

        output.Flush();
    }
}
