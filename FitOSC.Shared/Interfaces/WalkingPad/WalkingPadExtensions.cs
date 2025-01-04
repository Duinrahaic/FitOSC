namespace FitOSC.Shared.Interfaces.WalkingPad;

internal static class WalkingPadExtensions
{
    private static int[] FixCrc(this int[] cmd)
    { 
        if (cmd == null)
        {
            throw new ArgumentNullException(nameof(cmd), "Input command array cannot be null.");
        }

        if (cmd.Length < 3)
        {
            throw new ArgumentException("The cmd array must contain at least 3 bytes.", nameof(cmd));
        }

        int sum = 0;
        for (var i = 1; i < cmd.Length - 2; i++)
        {
            sum += cmd[i];
        }

        cmd[^2] = (byte)(sum % 256);

        return cmd;
    }

    public static byte[] GeneratePayload(int code, int parameter)
    {
        var payload = new int[6]
        {
            247, 162, code, parameter, 255, 253
        };
        return payload.FixCrc().Select(x => (byte)x).ToArray();
    }
   
}