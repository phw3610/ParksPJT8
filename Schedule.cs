using System;
using System.Globalization;
using System.Text;

namespace ScreenRecorder
{
    // When to record on its own: once between two moments, or every week on chosen
    // weekdays between two times of day (a window may run past midnight).
    internal sealed class Schedule
    {
        private const string DayLetters = "일월화수목금토";   // indexed by DayOfWeek

        public bool Weekly;
        public DateTime OnceStart, OnceEnd;
        public int Days;                    // one bit per DayOfWeek (Sunday = bit 0)
        public TimeSpan From, To;

        public static Schedule Once(DateTime start, DateTime end)
        {
            return new Schedule { Weekly = false, OnceStart = start, OnceEnd = end };
        }

        public static Schedule Every(int days, TimeSpan from, TimeSpan to)
        {
            return new Schedule { Weekly = true, Days = days, From = from, To = to };
        }

        public static bool HasDay(int days, DayOfWeek day)
        {
            return (days & (1 << (int)day)) != 0;
        }

        // Null when the schedule can be used, otherwise why not.
        public string Problem(DateTime now)
        {
            if (!Weekly)
            {
                if (OnceEnd <= OnceStart) return "종료 시각이 시작 시각보다 늦어야 합니다.";
                if (OnceEnd <= now) return "종료 시각이 이미 지났습니다.";
                return null;
            }

            if ((Days & 0x7F) == 0) return "요일을 하나 이상 선택하세요.";
            if (From == To) return "시작과 종료 시각이 같습니다.";
            return null;
        }

        // The window that is running now or the next one to come: the first whose end is
        // later than both `now` and `doneThrough` (the end of the last window already handled).
        // False when there is none left (a one-off in the past).
        public bool Next(DateTime now, DateTime doneThrough, out DateTime start, out DateTime end)
        {
            start = end = DateTime.MinValue;
            if (!Weekly)
            {
                if (OnceEnd <= now || OnceEnd <= doneThrough) return false;
                start = OnceStart;
                end = OnceEnd;
                return true;
            }

            TimeSpan length = To > From ? To - From : To + TimeSpan.FromDays(1) - From;
            for (int offset = -1; offset <= 7; offset++)   // -1: an overnight window that began yesterday
            {
                DateTime day = now.Date.AddDays(offset);
                if (!HasDay(Days, day.DayOfWeek)) continue;
                DateTime s = day + From;
                DateTime e = s + length;
                if (e > now && e > doneThrough)
                {
                    start = s;
                    end = e;
                    return true;
                }
            }

            return false;
        }

        // Short text for the status line, e.g. "월화수목금 09:00 → 18:00".
        public string Describe()
        {
            if (!Weekly)
            {
                string to = OnceEnd.Date == OnceStart.Date ? OnceEnd.ToString("HH:mm") : OnceEnd.ToString("M월 d일 HH:mm");
                return OnceStart.ToString("M월 d일 HH:mm") + " → " + to;
            }

            StringBuilder days = new StringBuilder();
            foreach (int d in new[] { 1, 2, 3, 4, 5, 6, 0 })   // Monday first
            {
                if (HasDay(Days, (DayOfWeek)d)) days.Append(DayLetters[d]);
            }

            string end = From.Hours.ToString("00") + ":" + From.Minutes.ToString("00") + " → "
                       + (To <= From ? "익일 " : string.Empty) + To.Hours.ToString("00") + ":" + To.Minutes.ToString("00");
            return days + " " + end;
        }

        public string Serialize()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return Weekly
                ? "weekly|" + Days.ToString(c) + "|" + From.ToString(@"hh\:mm", c) + "|" + To.ToString(@"hh\:mm", c)
                : "once|" + OnceStart.ToString("yyyy-MM-ddTHH:mm", c) + "|" + OnceEnd.ToString("yyyy-MM-ddTHH:mm", c);
        }

        // Null for empty or unreadable text.
        public static Schedule Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            CultureInfo c = CultureInfo.InvariantCulture;
            string[] part = text.Split('|');
            try
            {
                if (part.Length == 4 && part[0] == "weekly")
                {
                    return Every(int.Parse(part[1], c) & 0x7F,
                        TimeSpan.ParseExact(part[2], @"hh\:mm", c), TimeSpan.ParseExact(part[3], @"hh\:mm", c));
                }

                if (part.Length == 3 && part[0] == "once")
                {
                    return Once(DateTime.ParseExact(part[1], "yyyy-MM-ddTHH:mm", c),
                                DateTime.ParseExact(part[2], "yyyy-MM-ddTHH:mm", c));
                }
            }
            catch (FormatException) { }
            catch (OverflowException) { }
            return null;
        }
    }
}
