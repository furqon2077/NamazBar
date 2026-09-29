// Расчёт времени намаза — порт Astro из NamazBar.cs (adhan-js 4.4, параметры islom.uz):
// угол Фаджр/Иша 15.5°, мазхаб Ханафи, Шом = закат + 4 мин. Должен давать те же минуты, что и Windows-версия.
import Foundation

enum Astro {
    static func d2r(_ d: Double) -> Double { d * .pi / 180 }
    static func r2d(_ r: Double) -> Double { r * 180 / .pi }
    static func norm(_ n: Double, _ max: Double) -> Double { n - max * (n / max).rounded(.down) }
    static func unwind(_ a: Double) -> Double { norm(a, 360) }
    static func quadShift(_ a: Double) -> Double {
        if a >= -180 && a <= 180 { return a }
        return a - 360 * (a / 360).rounded(.toNearestOrAwayFromZero)
    }

    struct SolarCoords {
        var decl = 0.0, ra = 0.0, sidereal = 0.0
        init(_ jd: Double) {
            let T = (jd - 2451545.0) / 36525
            let L0 = unwind(280.4664567 + 36000.76983 * T + 0.0003032 * T * T)
            let Lp = unwind(218.3165 + 481267.8813 * T)
            let Om = unwind(125.04452 - 1934.136261 * T + 0.0020708 * T * T + T * T * T / 450000)
            let M = unwind(357.52911 + 35999.05029 * T - 0.0001537 * T * T)
            let Mr = d2r(M)
            let C = (1.914602 - 0.004817 * T - 0.000014 * T * T) * sin(Mr)
                  + (0.019993 - 0.000101 * T) * sin(2 * Mr) + 0.000289 * sin(3 * Mr)
            let O2 = 125.04 - 1934.136 * T
            let lambda = d2r(unwind(L0 + C - 0.00569 - 0.00478 * sin(d2r(O2))))
            let JD = T * 36525 + 2451545.0
            let theta0 = unwind(280.46061837 + 360.98564736629 * (JD - 2451545)
                                + 0.000387933 * T * T - T * T * T / 38710000)
            let dPsi = -17.2 / 3600 * sin(d2r(Om)) - 1.32 / 3600 * sin(2 * d2r(L0))
                     - 0.23 / 3600 * sin(2 * d2r(Lp)) + 0.21 / 3600 * sin(2 * d2r(Om))
            let dEps = 9.2 / 3600 * cos(d2r(Om)) + 0.57 / 3600 * cos(2 * d2r(L0))
                     + 0.1 / 3600 * cos(2 * d2r(Lp)) - 0.09 / 3600 * cos(2 * d2r(Om))
            let eps0 = 23.439291 - 0.013004167 * T - 0.0000001639 * T * T + 0.0000005036 * T * T * T
            let epsApp = d2r(eps0 + 0.00256 * cos(d2r(O2)))
            decl = r2d(asin(sin(epsApp) * sin(lambda)))
            ra = unwind(r2d(atan2(cos(epsApp) * sin(lambda), cos(lambda))))
            sidereal = theta0 + dPsi * 3600 * cos(d2r(eps0 + dEps)) / 3600
        }
    }

    static func interp(_ y2: Double, _ y1: Double, _ y3: Double, _ n: Double) -> Double {
        let a = y2 - y1, b = y3 - y2, c = b - a
        return y2 + n / 2 * (a + b + n * c)
    }
    static func interpAngles(_ y2: Double, _ y1: Double, _ y3: Double, _ n: Double) -> Double {
        let a = unwind(y2 - y1), b = unwind(y3 - y2), c = b - a
        return y2 + n / 2 * (a + b + n * c)
    }
    static func altitude(_ phi: Double, _ delta: Double, _ H: Double) -> Double {
        r2d(asin(sin(d2r(phi)) * sin(d2r(delta)) + cos(d2r(phi)) * cos(d2r(delta)) * cos(d2r(H))))
    }
    static func julianDay(_ year: Int, _ month: Int, _ day: Int) -> Double {
        let Y = month > 2 ? year : year - 1
        let M = month > 2 ? month : month + 12
        let A = Y / 100
        let B = 2 - A + A / 4
        let i0 = Int((365.25 * Double(Y + 4716)).rounded(.down))
        let i1 = Int((30.6001 * Double(M + 1)).rounded(.down))
        return Double(i0 + i1 + day + B) - 1524.5
    }

    struct SolarTime {
        let s, p, n: SolarCoords
        let lat, lng, m0: Double
        let transit, sunrise, sunset: Double

        init(year: Int, month: Int, day: Int, lat: Double, lng: Double) {
            self.lat = lat; self.lng = lng
            let jd = julianDay(year, month, day)
            s = SolarCoords(jd); p = SolarCoords(jd - 1); n = SolarCoords(jd + 1)
            let Lw = -lng
            var m = norm((s.ra + Lw - s.sidereal) / 360, 1)
            let expected = norm((12.0 - lng / 15.0) / 24.0, 1)
            if m - expected > 0.5 { m -= 1 } else if expected - m > 0.5 { m += 1 }
            m0 = m
            let theta = unwind(s.sidereal + 360.985647 * m0)
            let a = unwind(interpAngles(s.ra, p.ra, n.ra, m0))
            let H = quadShift(theta - Lw - a)
            transit = (m0 + H / -360) * 24
            sunrise = SolarTime.hourAngle(s, p, n, lat, lng, m0, -50.0 / 60.0, false)
            sunset = SolarTime.hourAngle(s, p, n, lat, lng, m0, -50.0 / 60.0, true)
        }

        static func hourAngle(_ s: SolarCoords, _ p: SolarCoords, _ n: SolarCoords, _ lat: Double, _ lng: Double,
                              _ m0: Double, _ h0: Double, _ after: Bool) -> Double {
            let Lw = -lng
            let t1 = sin(d2r(h0)) - sin(d2r(lat)) * sin(d2r(s.decl))
            let t2 = cos(d2r(lat)) * cos(d2r(s.decl))
            let H0 = r2d(acos(t1 / t2))
            let m = after ? m0 + H0 / 360 : m0 - H0 / 360
            let theta = unwind(s.sidereal + 360.985647 * m)
            let a = unwind(interpAngles(s.ra, p.ra, n.ra, m))
            let delta = interp(s.decl, p.decl, n.decl, m)
            let H = theta - Lw - a
            let h = altitude(lat, delta, H)
            let dm = (h - h0) / (360 * cos(d2r(delta)) * cos(d2r(lat)) * sin(d2r(H)))
            return (m + dm) * 24
        }
        func hourAngle(_ h0: Double, after: Bool) -> Double {
            SolarTime.hourAngle(s, p, n, lat, lng, m0, h0, after)
        }
        func afternoon(_ shadow: Double) -> Double {
            let tangent = abs(lat - s.decl)
            let inverse = shadow + tan(d2r(tangent))
            return hourAngle(r2d(atan(1.0 / inverse)), after: true)
        }
    }

    static var utcCal: Calendar = { var c = Calendar(identifier: .gregorian); c.timeZone = TimeZone(identifier: "UTC")!; return c }()

    // TimeComponents.utcDate: часы (UTC, дробные) -> момент времени на указанную дату
    static func utc(_ y: Int, _ mo: Int, _ d: Int, _ hours: Double) -> Date? {
        if hours.isNaN { return nil }
        let h = Int(hours.rounded(.down))
        let m = Int(((hours - Double(h)) * 60).rounded(.down))
        let s = Int(((hours - (Double(h) + Double(m) / 60.0)) * 3600).rounded(.down))
        let base = utcCal.date(from: DateComponents(year: y, month: mo, day: d))!
        return base.addingTimeInterval(Double(h * 3600 + m * 60 + s))
    }
    static func roundMin(_ d: Date) -> Date {
        let t = d.timeIntervalSince1970.rounded(.down)
        let sec = Int(t) % 60
        return Date(timeIntervalSince1970: sec >= 30 ? t + Double(60 - sec) : t - Double(sec))
    }

    /// [0]Бомдод [1]Қуёш [2]Пешин [3]Аср [4]Шом [5]Хуфтон — моменты времени для местной даты `day`
    static func compute(_ day: Date, lat: Double, lng: Double) -> [Date] {
        let fajrAngle = 15.5, ishaAngle = 15.5, shadow = 2.0   // Hanafi
        let c = Calendar.current.dateComponents([.year, .month, .day], from: day)
        let (y, mo, d) = (c.year!, c.month!, c.day!)
        let st = SolarTime(year: y, month: mo, day: d, lat: lat, lng: lng)
        let tomorrowDay = Calendar.current.date(byAdding: .day, value: 1, to: day)!
        let tc = Calendar.current.dateComponents([.year, .month, .day], from: tomorrowDay)
        let tst = SolarTime(year: tc.year!, month: tc.month!, day: tc.day!, lat: lat, lng: lng)

        let dhuhr = utc(y, mo, d, st.transit)!
        let sunrise = utc(y, mo, d, st.sunrise)!
        let sunset = utc(y, mo, d, st.sunset)!
        let asr = utc(y, mo, d, st.afternoon(shadow))!
        let tSunrise = utc(tc.year!, tc.month!, tc.day!, tst.sunrise)!
        let night = tSunrise.timeIntervalSince(sunset)

        var fajr = utc(y, mo, d, st.hourAngle(-fajrAngle, after: false))
        let safeFajr = sunrise.addingTimeInterval(-0.5 * night)
        if fajr == nil || safeFajr > fajr! { fajr = safeFajr }
        var isha = utc(y, mo, d, st.hourAngle(-ishaAngle, after: true))
        let safeIsha = sunset.addingTimeInterval(0.5 * night)
        if isha == nil || safeIsha < isha! { isha = safeIsha }

        return [roundMin(fajr!), roundMin(sunrise), roundMin(dhuhr), roundMin(asr),
                roundMin(sunset).addingTimeInterval(4 * 60),   // Шом на islom.uz = закат + 4 мин
                roundMin(isha!)]
    }
}
