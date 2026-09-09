using System.Text.Json;

namespace CinemaBooking.Services;

public enum SeatStatus { Available, Held, Booked }

public record SeatDto(string Id, int Row, int Col, string Status, string? Holder);
public record BookingDto(string Code, string Name, List<string> SeatIds, DateTime CreatedAt);

/// <summary>영화/상영시간 정보 (추후 확장용. 지금은 비어 있어도 동작)</summary>
public record ShowInfo(string MovieTitle, string ShowTime, string PosterUrl);

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

/// <summary>디스크에 저장되는 내용. (선택 중인 좌석은 재시작 후 의미가 없으므로 예매 확정건만 저장)</summary>
public class PersistedState
{
    public Dictionary<string, BookingDto> Bookings { get; set; } = new();
    public bool SiteOpen { get; set; } = true;
    public ShowInfo Show { get; set; } = new("", "", "");
}

/// <summary>
/// 좌석 30석의 상태 저장소.
/// - 모든 접근은 lock 으로 보호 (동시 클릭 시 한 명만 성공)
/// - 예매 확정/취소/설정 변경 시마다 JSON 파일로 저장 → 서버를 재시작해도 데이터 유지
/// </summary>
public class SeatStore
{
    public const int Rows = 5;
    public const int Cols = 6;
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Dictionary<string, Seat> _seats = new();
    private readonly string _dataFile;
    private PersistedState _state = new();
    private readonly ILogger<SeatStore> _log;

    public SeatStore(IConfiguration config, ILogger<SeatStore> log)
    {
        _log = log;
        // 환경변수 DATA_DIR 이 있으면 그 폴더에, 없으면 프로젝트의 Data 폴더에 저장
        var dir = config["DATA_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        _dataFile = Path.Combine(dir, "cinema.json");

        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                var id = $"{(char)('A' + r)}{c + 1}";
                _seats[id] = new Seat { Id = id, Row = r, Col = c };
            }

        Load();
    }

    // ---------- 저장 / 불러오기 ----------
    private void Load()
    {
        if (!File.Exists(_dataFile)) return;
        try
        {
            _state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_dataFile)) ?? new();
            foreach (var b in _state.Bookings.Values)
                foreach (var id in b.SeatIds)
                    if (_seats.TryGetValue(id, out var s)) { s.Status = SeatStatus.Booked; s.BookingCode = b.Code; }
            _log.LogInformation("저장된 예매 {n}건을 불러왔습니다.", _state.Bookings.Count);
        }
        catch (Exception ex) { _log.LogError(ex, "데이터 파일을 읽지 못했습니다. 빈 상태로 시작합니다."); }
    }

    private void Save()
    {
        try
        {
            var tmp = _dataFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _dataFile, overwrite: true);   // 쓰다가 꺼져도 파일이 깨지지 않도록
        }
        catch (Exception ex) { _log.LogError(ex, "데이터 저장 실패"); }
    }

    // ---------- 조회 ----------
    public List<SeatDto> Snapshot()
    {
        lock (_lock)
            return _seats.Values.OrderBy(s => s.Row).ThenBy(s => s.Col)
                .Select(s => new SeatDto(s.Id, s.Row, s.Col, s.Status.ToString().ToLower(), s.HolderConnectionId)).ToList();
    }

    public bool SiteOpen { get { lock (_lock) return _state.SiteOpen; } }
    public ShowInfo Show { get { lock (_lock) return _state.Show; } }

    public List<BookingDto> AllBookings()
    {
        lock (_lock) return _state.Bookings.Values.OrderBy(b => b.CreatedAt).ToList();
    }

    // ---------- 손님 기능 ----------
    public bool TryHold(string seatId, string connectionId)
    {
        lock (_lock)
        {
            if (!_state.SiteOpen) return false;
            if (!_seats.TryGetValue(seatId, out var seat)) return false;
            if (seat.Status == SeatStatus.Booked) return false;
            if (seat.Status == SeatStatus.Held && seat.HolderConnectionId != connectionId) return false;
            seat.Status = SeatStatus.Held;
            seat.HolderConnectionId = connectionId;
            seat.HoldExpiresAt = DateTime.UtcNow + HoldDuration;
            return true;
        }
    }

    public bool Release(string seatId, string connectionId)
    {
        lock (_lock)
        {
            if (!_seats.TryGetValue(seatId, out var seat)) return false;
            if (seat.Status != SeatStatus.Held || seat.HolderConnectionId != connectionId) return false;
            Clear(seat);
            return true;
        }
    }

    public int ReleaseAllOf(string connectionId)
    {
        lock (_lock)
        {
            int n = 0;
            foreach (var s in _seats.Values.Where(s => s.Status == SeatStatus.Held && s.HolderConnectionId == connectionId)) { Clear(s); n++; }
            return n;
        }
    }

    public int ExpireHolds()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow; int n = 0;
            foreach (var s in _seats.Values.Where(s => s.Status == SeatStatus.Held && s.HoldExpiresAt < now)) { Clear(s); n++; }
            return n;
        }
    }

    public string? Confirm(string connectionId, string customerName)
    {
        lock (_lock)
        {
            if (!_state.SiteOpen) return null;
            var mine = _seats.Values.Where(s => s.Status == SeatStatus.Held && s.HolderConnectionId == connectionId).ToList();
            if (mine.Count == 0) return null;

            string code;
            do code = Random.Shared.Next(1000, 9999).ToString(); while (_state.Bookings.ContainsKey(code));

            foreach (var s in mine) { s.Status = SeatStatus.Booked; s.HolderConnectionId = null; s.HoldExpiresAt = null; s.BookingCode = code; }
            _state.Bookings[code] = new BookingDto(code, customerName, mine.Select(s => s.Id).ToList(), DateTime.UtcNow);
            Save();
            return code;
        }
    }

    public List<string>? CancelBooking(string code)
    {
        lock (_lock)
        {
            if (!_state.Bookings.Remove(code, out var booking)) return null;
            foreach (var id in booking.SeatIds) Clear(_seats[id]);
            Save();
            return booking.SeatIds;
        }
    }

    // ---------- 관리자(선생님) 기능 ----------
    /// <summary>모든 선택(임시 점유)을 강제로 풀어줌</summary>
    public int AdminReleaseAllHolds()
    {
        lock (_lock)
        {
            int n = 0;
            foreach (var s in _seats.Values.Where(s => s.Status == SeatStatus.Held)) { Clear(s); n++; }
            return n;
        }
    }

    /// <summary>예매를 전부 삭제하고 좌석을 처음 상태로</summary>
    public void AdminResetAll()
    {
        lock (_lock)
        {
            foreach (var s in _seats.Values) Clear(s);
            _state.Bookings.Clear();
            Save();
        }
    }

    /// <summary>사이트 열기/닫기 (닫으면 손님은 좌석 선택·결제 불가, 화면은 보임)</summary>
    public void AdminSetSiteOpen(bool open) { lock (_lock) { _state.SiteOpen = open; Save(); } }

    public void AdminSetShow(ShowInfo show) { lock (_lock) { _state.Show = show; Save(); } }

    private static void Clear(Seat s)
    {
        s.Status = SeatStatus.Available; s.HolderConnectionId = null; s.HoldExpiresAt = null; s.BookingCode = null;
    }
}
