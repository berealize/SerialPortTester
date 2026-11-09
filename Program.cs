using System;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

class Program
{
    // ===== 실행 옵션 (명령줄로 변경 가능) =====
    static int _baud = 9600;          // 검사 보드레이트
    static int _listenMs = 700;       // 수동 수신 대기 시간(ms)
    static bool _doTx = false;        // 송신(루프백) 테스트 수행 여부 -- 기본 꺼짐
    static bool _sweep = false;       // 무응답 포트에 대해 보드레이트 스윕
    static string _onlyPort = null;   // 특정 포트만 검사
    static bool _helpRequested = false;

    static readonly int[] SweepBauds = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };

    // 송신 테스트 패턴. 장치 명령으로 해석될 위험이 가장 낮은 형태를 쓴다.
    static readonly byte[] TxPattern = { 0x55, 0xAA, 0x55, 0xAA };

    static int Main(string[] args)
    {
        if (!ParseArgs(args))
            return _helpRequested ? 0 : 2;

        Console.WriteLine("========================================");
        Console.WriteLine("   PC 시리얼(COM) 포트 통신 진단기");
        Console.WriteLine("========================================");
        Console.WriteLine("설정 : " + _baud + " bps, 8-N-1 / 수신대기 " + _listenMs + "ms"
                          + " / 송신테스트 " + (_doTx ? "ON" : "OFF")
                          + " / 보드스윕 " + (_sweep ? "ON" : "OFF"));
        Console.WriteLine();

        string[] ports = SerialPort.GetPortNames();
        Array.Sort(ports, ComparePortName);

        if (_onlyPort != null)
        {
            ports = new string[] { _onlyPort };
            Console.WriteLine("지정된 포트만 검사합니다 : " + _onlyPort);
            Console.WriteLine();
        }
        else if (ports.Length == 0)
        {
            Console.WriteLine("검색된 COM 포트가 없습니다.");
            Pause();
            return 1;
        }
        else
        {
            Console.WriteLine("총 " + ports.Length + "개의 포트가 발견되었습니다. 진단을 시작합니다...");
            Console.WriteLine();
        }

        int okCount = 0;
        foreach (string portName in ports)
        {
            if (Diagnose(portName))
                okCount++;
            Console.WriteLine(new string('-', 52));
        }

        Console.WriteLine();
        Console.WriteLine("진단 완료 : 총 " + ports.Length + "개 중 " + okCount + "개에서 통신 신호를 확인했습니다.");
        if (!_doTx)
            Console.WriteLine("* 송신 테스트는 꺼져 있습니다. 양방향 확인은 --tx 옵션으로 수행하세요.");
        Pause();
        return 0;
    }

    /// <summary>포트 1개를 진단한다. 실제 통신 신호가 확인되면 true.</summary>
    static bool Diagnose(string portName)
    {
        Console.WriteLine("[" + portName + "]");

        SerialPort port = null;
        int[] errCount = new int[1];
        SerialErrorReceivedEventHandler errHandler = null;

        try
        {
            port = new SerialPort(portName, _baud, Parity.None, 8, StopBits.One);
            port.ReadTimeout = 200;
            port.WriteTimeout = 200;
            // RTS/DTR 을 수동 제어하려면 흐름제어를 꺼야 한다.
            // Handshake.RequestToSend 상태에서는 CTS 가 Low 일 때 Write 가 블로킹된다.
            port.Handshake = Handshake.None;

            // ---------- 1단계 : 포트 열기 ----------
            port.Open();
            Console.WriteLine("  1) 포트 열기    : 성공  -> 드라이버 정상, 점유 없음");

            errHandler = delegate (object s, SerialErrorReceivedEventArgs e) { errCount[0]++; };
            port.ErrorReceived += errHandler;

            // ---------- 2단계 : 제어 신호선 상태 ----------
            Console.WriteLine("  2) 신호선 상태  : DSR=" + OnOff(port.DsrHolding)
                              + "  CTS=" + OnOff(port.CtsHolding)
                              + "  DCD=" + OnOff(port.CDHolding));
            Console.WriteLine("     +- 신호선 반응 : " + ProbeControlLines(port));

            // ---------- 3단계 : 수동 수신 (상대가 보내는 데이터 청취) ----------
            errCount[0] = 0;
            port.DiscardInBuffer();
            Thread.Sleep(_listenMs);
            int avail = port.BytesToRead;

            bool gotData = false;
            if (avail > 0)
            {
                byte[] buf = new byte[Math.Min(avail, 64)];
                int read = port.Read(buf, 0, buf.Length);
                Console.WriteLine("  3) 수동 수신    : " + avail + " 바이트 수신  -> 통신 확인 (상대 장치가 송신 중)");
                Console.WriteLine("     +- HEX  : " + ToHex(buf, read));
                Console.WriteLine("     +- TEXT : " + ToSafeText(buf, read));
                gotData = true;
            }
            else if (errCount[0] > 0)
            {
                Console.WriteLine("  3) 수동 수신    : 데이터 없음 / 프레이밍 오류 " + errCount[0] + "건"
                                  + "  -> 신호는 들어오는데 보드레이트가 맞지 않습니다");
            }
            else
            {
                Console.WriteLine("  3) 수동 수신    : " + _listenMs
                                  + "ms 동안 수신 없음 (상대가 먼저 보내지 않는 장치일 수 있음)");
            }

            // ---------- 3-b단계 : 보드레이트 스윕 ----------
            if (!gotData && _sweep)
            {
                int found = SweepBaudRate(port, errCount);
                if (found > 0)
                {
                    Console.WriteLine("     +- 스윕 결과 : " + found + " bps 에서 정상 데이터를 수신했습니다");
                    gotData = true;
                }
                else
                {
                    Console.WriteLine("     +- 스윕 결과 : 모든 보드레이트에서 유효한 데이터 없음");
                }
                try { port.BaudRate = _baud; }
                catch { }
            }

            // ---------- 4단계 : 송신 / 루프백 ----------
            if (_doTx)
            {
                if (DoTxTest(port))
                    gotData = true;
            }
            else
            {
                Console.WriteLine("  4) 송신 테스트  : 건너뜀 (--tx 옵션으로 활성화)");
            }

            // ---------- 종합 판정 ----------
            Console.WriteLine("  => 판정 : " + (gotData
                ? "통신 가능 확인됨"
                : "포트는 사용 가능하나, 실제 통신은 확인되지 않음"));

            return gotData;
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine("  1) 포트 열기    : 실패 (다른 프로그램이 이미 사용 중)");
            Console.WriteLine("  => 판정 : 점유 중 - 해당 프로그램을 종료한 뒤 다시 시도하세요");
            return false;
        }
        catch (IOException ex)
        {
            // 존재하지 않는 포트도 IOException 으로 올라오므로 등록 여부를 따로 확인한다.
            bool registered = Array.IndexOf(SerialPort.GetPortNames(), portName) >= 0;
            Console.WriteLine("  1) 포트 열기    : 실패 (" + ex.Message + ")");
            Console.WriteLine("  => 판정 : " + (registered
                ? "포트는 등록되어 있으나 장치가 응답하지 않습니다 (USB 변환기 분리 등)"
                : "이 PC에 존재하지 않는 포트입니다"));
            return false;
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine("  1) 포트 열기    : 실패 (" + ex.Message + ")");
            Console.WriteLine("  => 판정 : 존재하지 않는 포트 이름입니다");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ! 오류 : " + ex.GetType().Name + " - " + ex.Message);
            Console.WriteLine("  => 판정 : 진단 실패");
            return false;
        }
        finally
        {
            if (port != null)
            {
                try
                {
                    if (errHandler != null)
                        port.ErrorReceived -= errHandler;
                    if (port.IsOpen)
                        port.Close();
                }
                catch { }
                port.Dispose();
            }
        }
    }

    /// <summary>RTS/DTR 을 토글하고 CTS/DSR 이 따라오는지 본다.</summary>
    static string ProbeControlLines(SerialPort port)
    {
        try
        {
            bool origRts = port.RtsEnable;
            bool origDtr = port.DtrEnable;
            try
            {
                port.RtsEnable = false;
                port.DtrEnable = false;
                Thread.Sleep(60);
                bool ctsLow = port.CtsHolding;
                bool dsrLow = port.DsrHolding;

                port.RtsEnable = true;
                port.DtrEnable = true;
                Thread.Sleep(60);
                bool ctsHigh = port.CtsHolding;
                bool dsrHigh = port.DsrHolding;

                bool ctsFollows = (!ctsLow && ctsHigh);
                bool dsrFollows = (!dsrLow && dsrHigh);

                if (ctsFollows && dsrFollows)
                    return "RTS->CTS, DTR->DSR 모두 추종 -> 루프백 커넥터이거나 핀 되돌림 배선";
                if (ctsFollows)
                    return "RTS->CTS 추종 -> 하드웨어 흐름제어 배선 또는 루프백";
                if (dsrFollows)
                    return "DTR->DSR 추종 -> 핀 되돌림 배선";
                if (port.CtsHolding || port.DsrHolding)
                    return "토글에는 무반응이나 신호선 활성 -> 상대 장치 전원 켜짐 추정";
                return "반응 없음 (상대 장치 없음 또는 3선식 배선)";
            }
            finally
            {
                port.RtsEnable = origRts;
                port.DtrEnable = origDtr;
            }
        }
        catch (Exception ex)
        {
            return "확인 불가 (" + ex.GetType().Name + ")";
        }
    }

    /// <summary>보드레이트를 바꿔가며 유효한 데이터를 찾는다. 성공한 보드레이트(없으면 0).</summary>
    static int SweepBaudRate(SerialPort port, int[] errCount)
    {
        int slice = Math.Max(150, _listenMs / 2);
        foreach (int b in SweepBauds)
        {
            if (b == _baud)
                continue;
            try
            {
                port.BaudRate = b;
                errCount[0] = 0;
                port.DiscardInBuffer();
                Thread.Sleep(slice);
                int n = port.BytesToRead;
                if (n > 0 && errCount[0] == 0)
                {
                    byte[] buf = new byte[Math.Min(n, 32)];
                    int read = port.Read(buf, 0, buf.Length);
                    Console.WriteLine("     +- " + b + " bps : " + n + "B  " + ToHex(buf, read));
                    return b;
                }
            }
            catch { }
        }
        return 0;
    }

    /// <summary>송신 후 회신 데이터가 있는지 본다. 통신이 확인되면 true.</summary>
    static bool DoTxTest(SerialPort port)
    {
        try
        {
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            port.Write(TxPattern, 0, TxPattern.Length);

            int waited = 0;
            while (waited < 400 && port.BytesToRead < TxPattern.Length)
            {
                Thread.Sleep(20);
                waited += 20;
            }

            int n = port.BytesToRead;
            if (n == 0)
            {
                Console.WriteLine("  4) 송신 테스트  : 송신 성공(드라이버 수락), 회신 없음"
                                  + "  -> TX 경로 정상, 수신 여부는 판단 불가");
                return false;
            }

            byte[] buf = new byte[Math.Min(n, 64)];
            int read = port.Read(buf, 0, buf.Length);

            bool echo = (read >= TxPattern.Length);
            for (int i = 0; echo && i < TxPattern.Length; i++)
            {
                if (buf[i] != TxPattern[i])
                    echo = false;
            }

            if (echo)
            {
                Console.WriteLine("  4) 송신 테스트  : 보낸 패턴이 그대로 회신 -> 루프백 확인, TX/RX 모두 정상");
            }
            else
            {
                Console.WriteLine("  4) 송신 테스트  : " + read + " 바이트 회신 -> 상대 장치가 응답 (양방향 통신 가능)");
                Console.WriteLine("     +- HEX  : " + ToHex(buf, read));
                Console.WriteLine("     +- TEXT : " + ToSafeText(buf, read));
            }
            return true;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("  4) 송신 테스트  : 송신 타임아웃 -> 흐름제어 신호(CTS) 대기 중일 수 있습니다");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  4) 송신 테스트  : 실패 (" + ex.GetType().Name + " - " + ex.Message + ")");
            return false;
        }
    }

    // ===== 유틸 =====

    static string OnOff(bool v)
    {
        return v ? "ON " : "off";
    }

    static string ToHex(byte[] buf, int len)
    {
        StringBuilder sb = new StringBuilder(len * 3);
        for (int i = 0; i < len; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(buf[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    static string ToSafeText(byte[] buf, int len)
    {
        StringBuilder sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = buf[i];
            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        }
        return sb.ToString();
    }

    static int ComparePortName(string a, string b)
    {
        return NumOf(a).CompareTo(NumOf(b));
    }

    static int NumOf(string p)
    {
        int i = 0;
        while (i < p.Length && (p[i] < '0' || p[i] > '9'))
            i++;
        int n;
        return int.TryParse(p.Substring(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
    }

    static void Pause()
    {
        Console.WriteLine();
        Console.WriteLine("[종료하려면 아무 키나 누르세요]");
        try
        {
            Console.ReadKey(true);
        }
        catch
        {
            // 콘솔 입력이 리다이렉트된 환경에서는 무시
        }
    }

    static bool ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-b":
                case "--baud":
                    if (!TryNext(args, ref i, out _baud))
                        return Fail("--baud 에 숫자 값이 필요합니다");
                    break;
                case "-l":
                case "--listen":
                    if (!TryNext(args, ref i, out _listenMs))
                        return Fail("--listen 에 숫자 값이 필요합니다");
                    break;
                case "-p":
                case "--port":
                    if (i + 1 >= args.Length)
                        return Fail("--port 에 포트 이름이 필요합니다");
                    _onlyPort = args[++i];
                    break;
                case "--tx":
                    _doTx = true;
                    break;
                case "--sweep":
                    _sweep = true;
                    break;
                case "-h":
                case "--help":
                case "/?":
                    _helpRequested = true;
                    Usage();
                    return false;
                default:
                    return Fail("알 수 없는 옵션 : " + a);
            }
        }
        return true;
    }

    static bool TryNext(string[] args, ref int i, out int value)
    {
        value = 0;
        if (i + 1 >= args.Length)
            return false;
        return int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    static bool Fail(string msg)
    {
        Console.WriteLine("오류 : " + msg);
        Console.WriteLine();
        Usage();
        return false;
    }

    static void Usage()
    {
        Console.WriteLine("사용법 : SerialPortScanner.exe [옵션]");
        Console.WriteLine("  -b, --baud <bps>    검사 보드레이트 (기본 9600)");
        Console.WriteLine("  -l, --listen <ms>   수동 수신 대기 시간 (기본 700)");
        Console.WriteLine("  -p, --port <COMn>   특정 포트만 검사");
        Console.WriteLine("      --tx            송신/루프백 테스트 수행 (장치에 데이터를 보냅니다)");
        Console.WriteLine("      --sweep         무응답 시 보드레이트 스윕");
        Console.WriteLine("  -h, --help          이 도움말");
    }
}
