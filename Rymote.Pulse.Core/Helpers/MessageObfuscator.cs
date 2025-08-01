using System.Text;

namespace Rymote.Pulse.Core.Helpers;

public class MessageObfuscator
{
    public static byte[] Encrypt(byte[] inputBytes, byte[] keyBytes)
    {
        byte[] outputBytes = new byte[inputBytes.Length];
        for (int index = 0; index < inputBytes.Length; index++)
            outputBytes[index] = (byte)(inputBytes[index] ^ keyBytes[index % keyBytes.Length]);
        
        return outputBytes;
    }

    public static byte[] Decrypt(byte[] encryptedBytes, byte[] keyBytes)
    {
        return Encrypt(encryptedBytes, keyBytes); 
    }
}