use chrono::{DateTime, Utc};
use netlights_core::history::LocationHistory;
use std::collections::BTreeMap;
struct IpCheck<'a> {
    at: DateTime<Utc>,
    order: usize,
    ip: &'a str,
}

/// The latest matching observation is selected inside the stay, with a closed
/// stay's right boundary excluded. The next IP mustn't be attributed to it.
pub fn build(
    history: &LocationHistory,
    start: DateTime<Utc>,
    end: DateTime<Utc>,
) -> Vec<(usize, Option<String>)> {
    // Compacted observations retain two actual check times. An interior time
    // is not an additional measured IP and mustn't win a last-check query.
    let mut indexes: BTreeMap<&str, Vec<IpCheck<'_>>> = BTreeMap::new();
    for (i, o) in history.observations.iter().enumerate() {
        if let (Some(code), Some(ip)) = (o.country_code.as_deref(), o.public_ip.as_deref()) {
            let points = indexes.entry(code).or_default();
            points.push(IpCheck {
                at: o.observed_utc,
                order: i * 2,
                ip,
            });
            if o.until() != o.observed_utc {
                points.push(IpCheck {
                    at: o.until(),
                    order: i * 2 + 1,
                    ip,
                });
            }
        }
    }
    for points in indexes.values_mut() {
        points.sort_by_key(|check| (check.at, check.order));
    }
    history
        .stays
        .iter()
        .enumerate()
        .rev()
        .filter(|(_, s)| {
            let until = s
                .ended_utc
                .unwrap_or(history.last_observed_utc.unwrap_or(s.started_utc));
            s.started_utc <= end
                && if s.ended_utc.is_some() && until > s.started_utc {
                    until > start
                } else {
                    until >= start
                }
        })
        .map(|(i, s)| {
            let until = s
                .ended_utc
                .unwrap_or(history.last_observed_utc.unwrap_or(s.started_utc));
            let public_ip = indexes.get(s.iso.as_str()).and_then(|points| {
                let exclusive = s.ended_utc.is_some() && until > s.started_utc;
                let right = points.partition_point(|check| {
                    if exclusive {
                        check.at < until
                    } else {
                        check.at <= until
                    }
                });
                right
                    .checked_sub(1)
                    .and_then(|last| points.get(last))
                    .filter(|check| check.at >= s.started_utc)
                    .map(|check| check.ip.to_string())
            });
            (i, public_ip)
        })
        .collect()
}
#[cfg(test)]
mod tests {
    use super::build;
    use chrono::{Duration, Utc};
    use netlights_core::history::{LocationHistory, LocationObservation, LocationStay};
    #[test]
    fn period_start_excludes_an_ended_interval_but_keeps_a_point() {
        let start = Utc::now();
        let history = LocationHistory {
            stays: vec![
                LocationStay {
                    iso: "DE".into(),
                    started_utc: start - Duration::hours(1),
                    ended_utc: Some(start),
                },
                LocationStay {
                    iso: "FR".into(),
                    started_utc: start,
                    ended_utc: Some(start),
                },
            ],
            ..Default::default()
        };
        let rows = build(&history, start, start + Duration::hours(1));
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].0, 1);
    }
    fn observation(
        at: chrono::DateTime<Utc>,
        until: chrono::DateTime<Utc>,
        code: &str,
        ip: &str,
    ) -> LocationObservation {
        serde_json::from_value(serde_json::json!({"observedUtc":at,"observedUntilUtc":until,"countryCode":code,"publicIp":ip,"confirmed":true})).unwrap()
    }
    #[test]
    fn adjacent_same_country_stays_never_borrow_the_next_ip() {
        let a = Utc::now();
        let b = a + Duration::hours(1);
        let c = b + Duration::hours(1);
        let history = LocationHistory {
            stays: vec![
                LocationStay {
                    iso: "DE".into(),
                    started_utc: a,
                    ended_utc: Some(b),
                },
                LocationStay {
                    iso: "DE".into(),
                    started_utc: b,
                    ended_utc: Some(c),
                },
            ],
            observations: vec![
                observation(a, b, "DE", "198.51.100.13"),
                observation(b, c, "DE", "198.51.100.14"),
            ],
            ..Default::default()
        };
        let rows = build(&history, a, c);
        assert_eq!(rows[0].1.as_deref(), Some("198.51.100.14"));
        assert_eq!(rows[1].1.as_deref(), Some("198.51.100.13"));
    }
    #[test]
    fn unmatched_rows_and_overlapping_intervals_do_not_hide_the_matching_ip() {
        let a = Utc::now();
        let b = a + Duration::hours(1);
        let mut observations = vec![observation(a, b, "DE", "198.51.100.1")];
        for i in 1..6 {
            observations.push(observation(
                a + Duration::minutes(i),
                a + Duration::minutes(i),
                if i % 2 == 0 { "FR" } else { "DE" },
                "198.51.100.2",
            ));
        }
        let history = LocationHistory {
            stays: vec![LocationStay {
                iso: "DE".into(),
                started_utc: a,
                ended_utc: Some(b + Duration::minutes(1)),
            }],
            observations,
            ..Default::default()
        };
        assert_eq!(build(&history, a, b)[0].1.as_deref(), Some("198.51.100.1"));
    }
    #[test]
    fn point_stay_includes_its_single_observation_but_not_an_earlier_ending_interval() {
        let a = Utc::now();
        let history = LocationHistory {
            stays: vec![LocationStay {
                iso: "RU".into(),
                started_utc: a,
                ended_utc: Some(a),
            }],
            observations: vec![observation(a, a, "RU", "198.51.100.7")],
            ..Default::default()
        };
        assert_eq!(
            build(&history, a - Duration::hours(1), a)[0].1.as_deref(),
            Some("198.51.100.7")
        );
    }
    #[test]
    fn a_missing_ip_does_not_erase_the_last_recorded_ip() {
        let a = Utc::now();
        let b = a + Duration::minutes(1);
        let mut unknown = observation(b, b, "DE", "198.51.100.2");
        unknown.public_ip = None;
        let history = LocationHistory {
            stays: vec![LocationStay {
                iso: "DE".into(),
                started_utc: a,
                ended_utc: None,
            }],
            last_observed_utc: Some(b),
            observations: vec![observation(a, a, "DE", "198.51.100.1"), unknown],
            ..Default::default()
        };
        assert_eq!(build(&history, a, b)[0].1.as_deref(), Some("198.51.100.1"));
    }
    #[test]
    fn an_interval_without_a_recorded_check_cannot_supply_a_fabricated_ip() {
        let a = Utc::now();
        let b = a + Duration::hours(1);
        let history = LocationHistory {
            stays: vec![LocationStay {
                iso: "DE".into(),
                started_utc: a + Duration::minutes(30),
                ended_utc: Some(a + Duration::minutes(40)),
            }],
            observations: vec![observation(a, b, "DE", "198.51.100.1")],
            ..Default::default()
        };
        assert_eq!(build(&history, a, b)[0].1, None);
    }
}
