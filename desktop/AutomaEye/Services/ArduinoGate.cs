using System;
using System.IO.Ports;
using System.Threading;

namespace AutomaEye.Services;

/// <summary>
/// Serial control for an Arduino/PLC slide-gate. Firmware contract: 'O' opens
/// the gate, 'C' closes it; raw bytes written via Write() (e.g. the fixed
/// "0\n"/"1\n" OK/NG signal, or whatever a custom output script sends) pass
/// straight through.
/// </summary>
public class ArduinoGate : ISerialWriter, IDisposable
{
    private readonly SerialPort _port;

    public ArduinoGate(string portName, int baud)
    {
        _port = new SerialPort(portName, baud);
        _port.Open();
        Thread.Sleep(2000); // let the Arduino finish its reset-on-open
    }

    public void Open() => _port.Write("O");
    public void Close() => _port.Write("C");

    public void Cycle(int holdMs)
    {
        Close();
        Thread.Sleep(holdMs);
        Open();
    }

    public void Write(string text) => _port.Write(text);

    public void Dispose()
    {
        if (_port.IsOpen) _port.Close();
        _port.Dispose();
    }
}
