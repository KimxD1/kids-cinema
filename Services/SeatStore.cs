using System.Text.Json;

namespace CinemaBooking.Services;

public enum SeatStatus { Available, Held, Booked }

public record SeatDto(string Id, int Row, int Col, string Status, string? Holder);
public record BookingDto(string Code, string Name, List<string> SeatIds, DateTime CreatedAt);

/// <summary>영화 세션(상영관) 요약 정보. 손님 화면의 "영화 선택" 목록과 관리자 화면에서 사용.</summary>
public record SessionInfo(string Id, string MovieTitle, string StartTime, string EndTime, int Available, int Held, int Booked);

public class Seat
{
    public required string Id { get; init; }
    public required int Row { get; init; }
    public required int Col { get; init; }
    public SeatStatus Status { get; set; } = SeatStatus.Available;
    public string? HolderConnectionId { get; set; }
    public DateTime? HoldExpiresAt { get; set; }
    public string? BookingCode { get; set; }
}

/// <summary>
/// 영화 세션(예: cinema_1) 하나가 소유하는 좌석 30석 + 예매 목록.
/// 세션마다 완전히 독립적으로 예매/좌석이 관리됩니다.
/// </summary>
public class CinemaSession
{
    public required string Id { get; init; }
    public string MovieTitle { get; set; } = "";
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "00:00";
    public Dictionary<string, Seat> Seats { get; } = new();
    public Dictionary<string, BookingDto> Bookings { get; } = new();

    public CinemaSession()
    {
        for (int r = 0; r < SeatStore.Rows; r++)
            for (int c = 0; c < SeatStore.Cols; c++)
            {
                var id = $"{(char)('A' + r)}{c + 1}";
                Seats[id] = new Seat { Id = id, Row = r, Col = c };
            }
    }
}

/// <summary>디스크에 저장되는 세션 정보(예매 확정건 + 상영정보만 저장. 좌석 hold 상태는 재시작 후 의미 없으므로 저장 안함)</summary>
public class PersistedSession
{
    public string MovieTitle { get; set; } = "";
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "00:00";
    public Dictionary<string, BookingDto> Bookings { get; set; } = new();
}

public class PersistedState
{
    public Dictionary<string, PersistedSession> Sessions { get; set; } = new();
    public bool SiteOpen { get; set; } = true;
}

/// <summary>
/// 영화 세션(영화 종류) 여러 개를 관리하는 저장소.
/// - 각 세션(cinema_1, cinema_2 ...)은 좌석 30석과 예매 목록을 독립적으로 가짐
/// - 모든 접근은 lock 으로 보호 (동시 클릭 시 한 명만 성공)
/// - 예매 확정/취소/설정 변경 시마다 JSON 파일로 저장 → 서버를 재시작해도 데이터 유지
/// </summary>
public class SeatStore
{
    public const int Rows = 5;
    public const int Cols = 6;
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(5);

    /// <summary>한 사람(연결)이 한 세션에서 동시에 선택(hold)할 수 있는 최대 좌석 수</summary>
    public const int MaxHoldPerConnection = 4;

    private readonly object _lock = new();
    private readonly Dictionary<string, CinemaSession> _sessions = new();
    private readonly string _dataFile;
    private bool _siteOpen = true;
    private readonly ILogger<SeatStore> _log;

    public SeatStore(IConfiguration config, ILogger<SeatStore> log)
    {
        _log = log;
        var dir = config["DATA_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        _dataFile = Path.Combine(dir, "cinema.json");

        Load();

        // 저장된 세션이 하나도 없으면(최초 실행) 기본 세션 하나를 만들어 둔다.
        if (_sessions.Count == 0)
            _sessions["cinema_1"] = new CinemaSession { Id = "cinema_1", MovieTitle = "cinema_1", StartTime = "00:00", EndTime = "00:00" };
    }

    // ---------- 저장 / 불러오기 ----------
    private void Load()
    {
        if (!File.Exists(_dataFile)) return;
        try
        {
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_dataFile)) ?? new();
            _siteOpen = state.SiteOpen;
            foreach (var (sid, psession) in state.Sessions)
            {
                var session = new CinemaSession
                {
                    Id = sid,
                    MovieTitle = psession.MovieTitle,
                    StartTime = psession.StartTime,
                    EndTime = psession.EndTime
                };
                foreach (var b in psession.Bookings.Values)
                {
                    session.Bookings[b.Code] = b;
                    foreach (var seatId in b.SeatIds)
                        if (session.Seats.TryGetValue(seatId, out var s)) { s.Status = SeatStatus.Booked; s.BookingCode = b.Code; }
                }
                _sessions[sid] = session;
            }
            _log.LogInformation("저장된 영화 세션 {n}개를 불러왔습니다.", _sessions.Count);
        }
        catch (Exception ex) { _log.LogError(ex, "데이터 파일을 읽지 못했습니다. 빈 상태로 시작합니다."); }
    }

    private void Save()
    {
        try
        {
            var state = new PersistedState { SiteOpen = _siteOpen };
            foreach (var (sid, session) in _sessions)
                state.Sessions[sid] = new PersistedSession
                {
                    MovieTitle = session.MovieTitle,
                    StartTime = session.StartTime,
                    EndTime = session.EndTime,
                    Bookings = session.Bookings
                };

            var tmp = _dataFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _dataFile, overwrite: true);   // 쓰다가 꺼져도 파일이 깨지지 않도록
        }
        catch (Exception ex) { _log.LogError(ex, "데이터 저장 실패"); }
    }

    public bool SiteOpen { get { lock (_lock) return _siteOpen; } }

    // ---------- 세션(영화 종류) 조회 ----------
    public List<SessionInfo> ListSessions()
    {
        lock (_lock)
            return _sessions.Values.OrderBy(s => s.Id).Select(ToInfo).ToList();
    }

    private static SessionInfo ToInfo(CinemaSession s) => new(
        s.Id, s.MovieTitle, s.StartTime, s.EndTime,
        s.Seats.Values.Count(x => x.Status == SeatStatus.Available),
        s.Seats.Values.Count(x => x.Status == SeatStatus.Held),
        s.Seats.Values.Count(x => x.Status == SeatStatus.Booked));

    public List<SeatDto> Snapshot(string sessionId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s)) return new();
            return s.Seats.Values.OrderBy(x => x.Row).ThenBy(x => x.Col)
                .Select(x => new SeatDto(x.Id, x.Row, x.Col, x.Status.ToString().ToLower(), x.HolderConnectionId)).ToList();
        }
    }

    public List<BookingDto> AllBookings(string sessionId)
    {
        lock (_lock)
            return _sessions.TryGetValue(sessionId, out var s) ? s.Bookings.Values.OrderBy(b => b.CreatedAt).ToList() : new();
    }

    // ---------- 손님 기능 ----------
    public bool TryHold(string sessionId, string seatId, string connectionId)
    {
        lock (_lock)
        {
            if (!_siteOpen) return false;
            if (!_sessions.TryGetValue(sessionId, out var session)) return false;
            if (!session.Seats.TryGetValue(seatId, out var seat)) return false;
            if (seat.Status == SeatStatus.Booked) return false;
            if (seat.Status == SeatStatus.Held && seat.HolderConnectionId != connectionId) return false;

            // 이미 내가 선택한 좌석을 다시 누른 경우(연장)가 아니라면 최대 선택 개수를 체크한다.
            if (seat.Status != SeatStatus.Held)
            {
                var myHeldCount = session.Seats.Values.Count(x => x.Status == SeatStatus.Held && x.HolderConnectionId == connectionId);
                if (myHeldCount >= MaxHoldPerConnection) return false; // 최대 좌석 수(4석) 초과
            }

            seat.Status = SeatStatus.Held;
            seat.HolderConnectionId = connectionId;
            seat.HoldExpiresAt = DateTime.UtcNow + HoldDuration;
            return true;
        }
    }

    public bool Release(string sessionId, string seatId, string connectionId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return false;
            if (!session.Seats.TryGetValue(seatId, out var seat)) return false;
            if (seat.Status != SeatStatus.Held || seat.HolderConnectionId != connectionId) return false;
            Clear(seat);
            return true;
        }
    }

    /// <summary>연결이 끊기면 모든 세션에서 이 사람이 선택 중이던 좌석을 해제한다. (세션ID → 해제 개수)</summary>
    public Dictionary<string, int> ReleaseAllOf(string connectionId)
    {
        lock (_lock)
        {
            var result = new Dictionary<string, int>();
            foreach (var (sid, session) in _sessions)
            {
                int n = 0;
                foreach (var s in session.Seats.Values.Where(x => x.Status == SeatStatus.Held && x.HolderConnectionId == connectionId)) { Clear(s); n++; }
                if (n > 0) result[sid] = n;
            }
            return result;
        }
    }

    /// <summary>5분 경과된 선택을 모두 해제한다. (세션ID → 해제 개수)</summary>
    public Dictionary<string, int> ExpireHolds()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var result = new Dictionary<string, int>();
            foreach (var (sid, session) in _sessions)
            {
                int n = 0;
                foreach (var s in session.Seats.Values.Where(x => x.Status == SeatStatus.Held && x.HoldExpiresAt < now)) { Clear(s); n++; }
                if (n > 0) result[sid] = n;
            }
            return result;
        }
    }

    public string? Confirm(string sessionId, string connectionId, string customerName)
    {
        lock (_lock)
        {
            if (!_siteOpen) return null;
            if (!_sessions.TryGetValue(sessionId, out var session)) return null;
            var mine = session.Seats.Values.Where(s => s.Status == SeatStatus.Held && s.HolderConnectionId == connectionId).ToList();
            if (mine.Count == 0) return null;

            string code;
            do code = Random.Shared.Next(1000, 9999).ToString(); while (_sessions.Values.Any(s => s.Bookings.ContainsKey(code)));

            foreach (var s in mine) { s.Status = SeatStatus.Booked; s.HolderConnectionId = null; s.HoldExpiresAt = null; s.BookingCode = code; }
            session.Bookings[code] = new BookingDto(code, customerName, mine.Select(s => s.Id).ToList(), DateTime.UtcNow);
            Save();
            return code;
        }
    }

    /// <summary>예매번호로 취소. 어느 세션인지 모르므로 전체 세션을 뒤져서 찾는다.</summary>
    public (string sessionId, List<string> seatIds)? CancelBooking(string code)
    {
        lock (_lock)
        {
            foreach (var (sid, session) in _sessions)
            {
                if (session.Bookings.Remove(code, out var booking))
                {
                    foreach (var id in booking.SeatIds) Clear(session.Seats[id]);
                    Save();
                    return (sid, booking.SeatIds);
                }
            }
            return null;
        }
    }

    // ---------- 관리자(선생님) 기능 ----------
    /// <summary>선택 중(노랑/빨강) 좌석을 해제. sessionId 를 주면 그 세션만, null 이면 전체 세션.</summary>
    public int AdminReleaseAllHolds(string? sessionId = null)
    {
        lock (_lock)
        {
            IEnumerable<CinemaSession> targets = sessionId == null
                ? _sessions.Values
                : (_sessions.TryGetValue(sessionId, out var s) ? new[] { s } : Array.Empty<CinemaSession>());

            int n = 0;
            foreach (var session in targets)
                foreach (var seat in session.Seats.Values.Where(x => x.Status == SeatStatus.Held)) { Clear(seat); n++; }
            return n;
        }
    }

    /// <summary>예매를 전부 삭제하고 좌석을 처음 상태로. sessionId 를 주면 그 세션만, null 이면 전체 세션.</summary>
    public void AdminResetAll(string? sessionId = null)
    {
        lock (_lock)
        {
            IEnumerable<CinemaSession> targets = sessionId == null
                ? _sessions.Values
                : (_sessions.TryGetValue(sessionId, out var s) ? new[] { s } : Array.Empty<CinemaSession>());

            foreach (var session in targets)
            {
                foreach (var seat in session.Seats.Values) Clear(seat);
                session.Bookings.Clear();
            }
            Save();
        }
    }

    /// <summary>사이트 열기/닫기 (닫으면 손님은 좌석 선택·결제 불가, 화면은 보임)</summary>
    public void AdminSetSiteOpen(bool open) { lock (_lock) { _siteOpen = open; Save(); } }

    /// <summary>영화 세션 추가 또는 수정 (id가 이미 있으면 정보만 갱신되고 좌석/예매는 그대로 유지)</summary>
    public bool AdminAddOrUpdateSession(string id, string title, string startTime, string endTime)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            id = id.Trim();
            if (!_sessions.TryGetValue(id, out var session))
            {
                session = new CinemaSession { Id = id };
                _sessions[id] = session;
            }
            session.MovieTitle = string.IsNullOrWhiteSpace(title) ? id : title.Trim();
            session.StartTime = string.IsNullOrWhiteSpace(startTime) ? "00:00" : startTime.Trim();
            session.EndTime = string.IsNullOrWhiteSpace(endTime) ? "00:00" : endTime.Trim();
            Save();
            return true;
        }
    }

    /// <summary>영화 세션 삭제. 예매가 하나라도 있으면 삭제 거부. 마지막 남은 하나는 삭제 거부.</summary>
    public bool AdminRemoveSession(string id)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out var session)) return false;
            if (session.Bookings.Count > 0) return false;
            if (_sessions.Count <= 1) return false;
            _sessions.Remove(id);
            Save();
            return true;
        }
    }

    private static void Clear(Seat s)
    {
        s.Status = SeatStatus.Available; s.HolderConnectionId = null; s.HoldExpiresAt = null; s.BookingCode = null;
    }
}
