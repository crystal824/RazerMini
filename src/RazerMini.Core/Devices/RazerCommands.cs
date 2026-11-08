namespace RazerMini.Core.Devices;

/// <summary>
/// 雷蛇命令帧（与 openrazer razerchromacommon.c 一一对应）。
/// 每条命令的常量都标注了出处函数，改之前先回源码核对。
/// </summary>
public static class RazerCommands
{
    // ── 电量 / 充电（openrazer: razer_chroma_misc_get_battery_level / get_charging_status）──
    public const byte ClassBattery = 0x07;
    public const byte IdBatteryLevel = 0x80;
    public const byte IdChargingStatus = 0x84;
    public const byte BatteryDataSize = 0x02;

    // ── DPI（openrazer: razer_chroma_misc_get_dpi_xy / set_dpi_xy）──
    public const byte ClassDpi = 0x04;
    public const byte IdGetDpi = 0x85;
    public const byte IdSetDpi = 0x05;
    public const byte DpiDataSize = 0x07;

    public const byte VariableStore = 0x01;   // VARSTORE
    public const byte NoStore = 0x00;         // NOSTORE（读当前值用这个）

    /// <summary>class 0x07 / id 0x80：读电量。dataSize 0x02，args 全 0，应答电量在 arguments[1]。</summary>
    public static byte[] GetBatteryLevel(byte transactionId) =>
        RazerReport.CreateFrame(transactionId, ClassBattery, IdBatteryLevel, BatteryDataSize);

    /// <summary>class 0x07 / id 0x84：读是否在充电。应答 arguments[1] = 0/1。</summary>
    public static byte[] GetChargingStatus(byte transactionId) =>
        RazerReport.CreateFrame(transactionId, ClassBattery, IdChargingStatus, BatteryDataSize);

    /// <summary>class 0x04 / id 0x85：读 DPI。args[0] = NOSTORE，应答 x = args[1..2]、y = args[3..4]（大端）。</summary>
    public static byte[] GetDpi(byte transactionId) =>
        RazerReport.CreateFrame(transactionId, ClassDpi, IdGetDpi, DpiDataSize, new[] { NoStore });

    /// <summary>
    /// class 0x04 / id 0x05：写 DPI。args[0] = VARSTORE、args[1..2] = X、args[3..4] = Y（大端）、args[5..6] = 0。
    /// </summary>
    public static byte[] SetDpi(byte transactionId, ushort dpiX, ushort dpiY)
    {
        var args = new byte[7];
        args[0] = VariableStore;
        args[1] = (byte)(dpiX >> 8);
        args[2] = (byte)(dpiX & 0xFF);
        args[3] = (byte)(dpiY >> 8);
        args[4] = (byte)(dpiY & 0xFF);
        args[5] = 0x00;
        args[6] = 0x00;
        return RazerReport.CreateFrame(transactionId, ClassDpi, IdSetDpi, DpiDataSize, args);
    }
}
