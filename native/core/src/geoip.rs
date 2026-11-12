use anyhow::{Context, Result, bail, ensure};
use std::{
    collections::HashMap,
    io::{Cursor, Read},
    net::IpAddr,
    sync::atomic::{AtomicBool, Ordering},
};

#[path = "geoip_store.rs"]
mod store;
pub use store::{DatabaseSnapshot, DatabaseSnapshots, DatabaseWorker};

pub const V2FLY_MAX_BYTES: usize = 40 * 1024 * 1024;
pub const IPLOCATE_MAX_BYTES: usize = 24 * 1024 * 1024;
const CSV_MAX_BYTES: u64 = 128 * 1024 * 1024;
const MAX_NETWORKS: usize = 2_500_000;

/// Keys and country indices are separate arrays: an IPv6 entry needs 17 bytes,
/// rather than the 32 bytes of an aligned (u128, u8) tuple.
#[derive(Default)]
struct Bucket<T> {
    keys: Vec<T>,
    countries: Vec<u8>,
}

pub struct CountryDatabase {
    ipv4: [Bucket<u32>; 33],
    ipv6: [Bucket<u128>; 129],
    countries: Vec<String>,
    pub network_count: usize,
}
impl CountryDatabase {
    pub fn lookup(&self, address: IpAddr) -> Option<&str> {
        match address {
            IpAddr::V4(ip) => {
                let value = u32::from(ip);
                for prefix in (0..=32).rev() {
                    let bucket = &self.ipv4[prefix];
                    if let Ok(index) = bucket.keys.binary_search(&mask4(value, prefix)) {
                        return Some(&self.countries[bucket.countries[index] as usize]);
                    }
                }
            }
            IpAddr::V6(ip) => {
                if let Some(ipv4) = ip.to_ipv4_mapped() {
                    return self.lookup(IpAddr::V4(ipv4));
                }
                let value = u128::from(ip);
                for prefix in (0..=128).rev() {
                    let bucket = &self.ipv6[prefix];
                    if let Ok(index) = bucket.keys.binary_search(&mask6(value, prefix)) {
                        return Some(&self.countries[bucket.countries[index] as usize]);
                    }
                }
            }
        }
        None
    }

    pub fn parse_v2fly(bytes: &[u8], cancel: &AtomicBool) -> Result<Self> {
        ensure!(
            !bytes.is_empty() && bytes.len() <= V2FLY_MAX_BYTES,
            "Invalid GeoIP payload size"
        );
        let mut builder = Builder::default();
        let mut reader = Proto::new(bytes);
        let mut entries = 0usize;
        let mut parsed = 0usize;
        while let Some((field, wire)) = reader.tag()? {
            checkpoint(cancel)?;
            if field != 1 || wire != 2 {
                reader.skip(wire)?;
                continue;
            }
            entries += 1;
            ensure!(entries <= 4096, "Too many GeoIP country entries");
            let message = reader.delimited()?;
            let mut entry = Proto::new(message);
            let mut code = None;
            while let Some((field, wire)) = entry.tag()? {
                if field == 1 && wire == 2 {
                    let value = entry.delimited()?;
                    ensure!(value.len() <= 64, "GeoIP country name is too long");
                    code = Some(std::str::from_utf8(value)?.to_owned());
                } else {
                    entry.skip(wire)?;
                }
            }
            let code = code.as_deref().and_then(normalize_country);
            let mut entry = Proto::new(message);
            while let Some((field, wire)) = entry.tag()? {
                if field != 2 || wire != 2 {
                    entry.skip(wire)?;
                    continue;
                }
                parsed += 1;
                ensure!(parsed <= 2_000_000, "Too many GeoIP networks");
                if parsed & 1023 == 0 {
                    checkpoint(cancel)?;
                }
                let (address, prefix) = parse_cidr(entry.delimited()?)?;
                if let Some(code) = &code {
                    builder.add(address, prefix, code)?;
                }
            }
        }
        builder.finish(cancel)
    }

    pub fn parse_iplocate(
        bytes: &[u8],
        minimum_networks: usize,
        minimum_per_family: usize,
        cancel: &AtomicBool,
    ) -> Result<Self> {
        ensure!(
            !bytes.is_empty() && bytes.len() <= IPLOCATE_MAX_BYTES,
            "Invalid IPLocate ZIP size"
        );
        checkpoint(cancel)?;
        validate_zip_entry_limit(bytes)?;
        let mut zip = zip::ZipArchive::new(Cursor::new(bytes))?;
        let mut selected = None;
        // A malformed ZIP with an enormous entry table is rejected before scanning it.
        ensure!(zip.len() <= 64, "Too many IPLocate ZIP entries");
        for index in 0..zip.len() {
            let entry = zip.by_index(index)?;
            let name = entry.name().to_ascii_lowercase();
            if name.starts_with("ip-to-country-") && name.ends_with(".csv") {
                ensure!(selected.is_none(), "IPLocate ZIP has multiple country CSVs");
                ensure!(
                    entry.size() > 0 && entry.size() <= CSV_MAX_BYTES,
                    "Invalid IPLocate CSV size"
                );
                selected = Some(index);
            }
        }
        let mut entry = zip.by_index(selected.context("IPLocate country CSV is missing")?)?;
        let mut builder = Builder::default();
        let mut line = Vec::with_capacity(256);
        let mut buffer = [0u8; 8192];
        let mut total = 0u64;
        let mut header = true;
        loop {
            checkpoint(cancel)?;
            let read = entry.read(&mut buffer)?;
            if read == 0 {
                break;
            }
            total = total.saturating_add(read as u64);
            ensure!(
                total <= CSV_MAX_BYTES,
                "IPLocate CSV exceeds expanded size limit"
            );
            for &byte in &buffer[..read] {
                if byte == b'\n' {
                    process_csv_line(&line, &mut header, &mut builder)?;
                    line.clear();
                } else {
                    ensure!(line.len() < 256, "IPLocate CSV line exceeds its size limit");
                    line.push(byte);
                }
            }
        }
        if !line.is_empty() {
            process_csv_line(&line, &mut header, &mut builder)?;
        }
        ensure!(
            !header && builder.count >= minimum_networks && minimum_networks > 0,
            "IPLocate CSV is incomplete"
        );
        ensure!(
            minimum_per_family > 0
                && builder.v4.iter().map(|b| b.keys.len()).sum::<usize>() >= minimum_per_family
                && builder.v6.iter().map(|b| b.keys.len()).sum::<usize>() >= minimum_per_family,
            "IPLocate CSV must contain substantial IPv4 and IPv6 coverage"
        );
        builder.finish(cancel)
    }
}

struct Builder {
    v4: [Bucket<u32>; 33],
    v6: [Bucket<u128>; 129],
    countries: Vec<String>,
    indexes: HashMap<String, u8>,
    count: usize,
}
impl Default for Builder {
    fn default() -> Self {
        Self {
            v4: std::array::from_fn(|_| Bucket::default()),
            v6: std::array::from_fn(|_| Bucket::default()),
            countries: Vec::new(),
            indexes: HashMap::new(),
            count: 0,
        }
    }
}
impl Builder {
    fn add(&mut self, address: IpAddr, prefix: usize, country: &str) -> Result<()> {
        ensure!(self.count < MAX_NETWORKS, "Too many country networks");
        let country = if let Some(index) = self.indexes.get(country) {
            *index
        } else {
            ensure!(self.countries.len() < 255, "Too many country codes");
            let index = self.countries.len() as u8;
            self.indexes.insert(country.to_owned(), index);
            self.countries.push(country.to_owned());
            index
        };
        match address {
            IpAddr::V4(ip) => {
                ensure!(prefix <= 32, "Invalid IPv4 prefix");
                self.v4[prefix].keys.push(mask4(u32::from(ip), prefix));
                self.v4[prefix].countries.push(country);
            }
            IpAddr::V6(ip) => {
                ensure!(prefix <= 128, "Invalid IPv6 prefix");
                self.v6[prefix].keys.push(mask6(u128::from(ip), prefix));
                self.v6[prefix].countries.push(country);
            }
        }
        self.count += 1;
        Ok(())
    }
    fn finish(mut self, cancel: &AtomicBool) -> Result<CountryDatabase> {
        ensure!(self.count > 0, "Country database has no usable networks");
        for bucket in &mut self.v4 {
            bucket.sort(cancel)?;
        }
        for bucket in &mut self.v6 {
            bucket.sort(cancel)?;
        }
        Ok(CountryDatabase {
            ipv4: self.v4,
            ipv6: self.v6,
            countries: self.countries,
            network_count: self.count,
        })
    }
}
impl<T: Ord + Copy> Bucket<T> {
    fn sort(&mut self, cancel: &AtomicBool) -> Result<()> {
        // Paired in-place heapsort bounds both peak memory and worst-case work.
        // Cancellation is checked throughout, including a single large bucket.
        let length = self.keys.len();
        for root in (0..length / 2).rev() {
            if root & 1023 == 0 {
                checkpoint(cancel)?;
            }
            self.sift(root, length);
        }
        for end in (1..length).rev() {
            if end & 1023 == 0 {
                checkpoint(cancel)?;
            }
            self.keys.swap(0, end);
            self.countries.swap(0, end);
            self.sift(0, end);
        }
        for index in 1..length {
            if index & 1023 == 0 {
                checkpoint(cancel)?;
            }
            ensure!(
                self.keys[index] != self.keys[index - 1]
                    || self.countries[index] == self.countries[index - 1],
                "Conflicting country networks with the same prefix"
            );
        }
        self.keys.shrink_to_fit();
        self.countries.shrink_to_fit();
        checkpoint(cancel)
    }
    fn sift(&mut self, mut root: usize, end: usize) {
        loop {
            let mut child = root * 2 + 1;
            if child >= end {
                return;
            }
            if child + 1 < end && self.keys[child] < self.keys[child + 1] {
                child += 1;
            }
            if self.keys[root] >= self.keys[child] {
                return;
            }
            self.keys.swap(root, child);
            self.countries.swap(root, child);
            root = child;
        }
    }
}
fn checkpoint(cancel: &AtomicBool) -> Result<()> {
    ensure!(
        !cancel.load(Ordering::Relaxed),
        "Country database operation cancelled"
    );
    Ok(())
}
fn validate_zip_entry_limit(bytes: &[u8]) -> Result<()> {
    // ZipArchive may backtrack to an earlier EOCD, even beyond the usual 64 KiB
    // comment window. Bound every candidate it can use before allocating a table.
    ensure!(bytes.len() >= 22, "Truncated IPLocate ZIP trailer");
    let mut exact_trailer = false;
    for position in 0..=bytes.len() - 22 {
        if bytes[position..position + 4] != [0x50, 0x4b, 0x05, 0x06] {
            continue;
        }
        let trailer = &bytes[position..];
        let read16 = |offset| u16::from_le_bytes([trailer[offset], trailer[offset + 1]]);
        let read32 = |offset| u32::from_le_bytes(trailer[offset..offset + 4].try_into().unwrap());
        let comment_end = position + 22 + usize::from(read16(20));
        if comment_end > bytes.len() {
            continue;
        }
        let locator =
            position >= 20 && bytes[position - 20..position - 16] == [0x50, 0x4b, 0x06, 0x07];
        if read16(4) == read16(6) || locator {
            ensure!(
                read16(8) <= 64 && read16(10) <= 64,
                "IPLocate ZIP entry count exceeds its limit"
            );
            ensure!(
                read32(12) != u32::MAX && read32(16) != u32::MAX && !locator,
                "IPLocate ZIP64 directory is unsupported"
            );
        }
        if comment_end == bytes.len() {
            ensure!(
                read16(4) == 0 && read16(6) == 0 && read16(8) == read16(10) && read16(10) <= 64,
                "IPLocate ZIP entry count or volume is unsupported"
            );
            exact_trailer = true;
        }
    }
    ensure!(exact_trailer, "IPLocate ZIP trailer is missing");
    Ok(())
}
fn mask4(value: u32, prefix: usize) -> u32 {
    if prefix == 0 {
        0
    } else {
        value & (u32::MAX << (32 - prefix))
    }
}
fn mask6(value: u128, prefix: usize) -> u128 {
    if prefix == 0 {
        0
    } else {
        value & (u128::MAX << (128 - prefix))
    }
}

/// ISO 3166 alpha-2 codes. Routing collections such as PRIVATE are not countries.
pub fn normalize_country(value: &str) -> Option<String> {
    if value.len() != 2 || !value.bytes().all(|b| b.is_ascii_alphabetic()) {
        return None;
    }
    let code = value.to_ascii_uppercase();
    crate::countries::valid(&code).then_some(code)
}
fn process_csv_line(bytes: &[u8], header: &mut bool, builder: &mut Builder) -> Result<()> {
    let line = std::str::from_utf8(bytes)?.trim_end_matches('\r');
    if *header {
        *header = false;
        ensure!(
            line.trim_start_matches('\u{feff}')
                .to_ascii_lowercase()
                .starts_with("network,"),
            "Invalid IPLocate CSV header"
        );
        return Ok(());
    }
    if line.is_empty() {
        return Ok(());
    }
    let mut fields = line.split(',');
    let Some(network) = fields.next() else {
        return Ok(());
    };
    let _continent = fields.next();
    let Some(country) = fields.next().filter(|country| {
        country.len() == 2 && country.bytes().all(|byte| byte.is_ascii_alphabetic())
    }) else {
        return Ok(());
    };
    let code = [
        country.as_bytes()[0].to_ascii_uppercase(),
        country.as_bytes()[1].to_ascii_uppercase(),
    ];
    let country = std::str::from_utf8(&code)?;
    if !crate::countries::valid(country) {
        return Ok(());
    }
    let Some((address, prefix)) = network.split_once('/') else {
        return Ok(());
    };
    let (Ok(address), Ok(prefix)) = (address.parse::<IpAddr>(), prefix.parse::<usize>()) else {
        return Ok(());
    };
    let bits = if address.is_ipv4() { 32 } else { 128 };
    if prefix <= bits {
        builder.add(address, prefix, country)?;
    }
    Ok(())
}

struct Proto<'a> {
    bytes: &'a [u8],
    position: usize,
}
impl<'a> Proto<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }
    fn varint(&mut self) -> Result<u64> {
        let mut value = 0u64;
        for shift in (0..70).step_by(7) {
            let byte = *self
                .bytes
                .get(self.position)
                .context("Truncated GeoIP varint")?;
            self.position += 1;
            ensure!(shift != 63 || byte <= 1, "GeoIP varint overflows");
            value |= u64::from(byte & 127) << shift;
            if byte & 128 == 0 {
                return Ok(value);
            }
        }
        bail!("GeoIP varint is too long")
    }
    fn tag(&mut self) -> Result<Option<(u64, u8)>> {
        if self.position == self.bytes.len() {
            return Ok(None);
        }
        let tag = self.varint()?;
        let (field, wire) = (tag >> 3, (tag & 7) as u8);
        ensure!(
            field > 0 && field <= 0x1fff_ffff && matches!(wire, 0 | 1 | 2 | 5),
            "Invalid GeoIP protobuf tag"
        );
        Ok(Some((field, wire)))
    }
    fn take(&mut self, length: usize) -> Result<&'a [u8]> {
        let end = self
            .position
            .checked_add(length)
            .context("GeoIP size overflow")?;
        let slice = self
            .bytes
            .get(self.position..end)
            .context("Truncated GeoIP protobuf")?;
        self.position = end;
        Ok(slice)
    }
    fn delimited(&mut self) -> Result<&'a [u8]> {
        let length = usize::try_from(self.varint()?)?;
        self.take(length)
    }
    fn skip(&mut self, wire: u8) -> Result<()> {
        match wire {
            0 => {
                self.varint()?;
            }
            1 => {
                self.take(8)?;
            }
            2 => {
                self.delimited()?;
            }
            5 => {
                self.take(4)?;
            }
            _ => bail!("Invalid protobuf wire type"),
        }
        Ok(())
    }
}
fn parse_cidr(bytes: &[u8]) -> Result<(IpAddr, usize)> {
    let mut reader = Proto::new(bytes);
    let mut address = None;
    let mut prefix = None;
    while let Some((field, wire)) = reader.tag()? {
        match (field, wire) {
            (1, 2) => {
                let raw = reader.delimited()?;
                address = Some(match raw.len() {
                    4 => IpAddr::V4(std::net::Ipv4Addr::from(<[u8; 4]>::try_from(raw)?)),
                    16 => IpAddr::V6(std::net::Ipv6Addr::from(<[u8; 16]>::try_from(raw)?)),
                    _ => bail!("Invalid GeoIP address length"),
                });
            }
            (2, 0) => {
                prefix = Some(usize::try_from(reader.varint()?)?);
            }
            _ => reader.skip(wire)?,
        }
    }
    let address = address.context("GeoIP CIDR has no address")?;
    let prefix = prefix.context("GeoIP CIDR has no prefix")?;
    ensure!(
        prefix <= if address.is_ipv4() { 32 } else { 128 },
        "Invalid GeoIP prefix"
    );
    Ok((address, prefix))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    fn stopped() -> AtomicBool {
        AtomicBool::new(false)
    }
    fn field(tag: u8, body: &[u8]) -> Vec<u8> {
        let mut v = vec![tag, body.len() as u8];
        v.extend_from_slice(body);
        v
    }
    fn country(code: &str, address: &[u8], prefix: u8) -> Vec<u8> {
        let mut cidr = field(10, address);
        cidr.extend([16, prefix]);
        let mut entry = field(10, code.as_bytes());
        entry.extend(field(18, &cidr));
        field(10, &entry)
    }
    fn archive(csv: &[u8]) -> Vec<u8> {
        let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
        writer
            .start_file(
                "ip-to-country-test.csv",
                zip::write::SimpleFileOptions::default()
                    .compression_method(zip::CompressionMethod::Deflated),
            )
            .unwrap();
        writer.write_all(csv).unwrap();
        writer.finish().unwrap().into_inner()
    }
    #[test]
    fn protobuf_matches_longest_ipv4_ipv6_and_mapped_prefix() {
        let mut bytes = country("DE", &[1, 0, 0, 0], 8);
        bytes.extend(country("FR", &[1, 2, 0, 0], 16));
        bytes.extend(country(
            "JP",
            &[0x20, 1, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            32,
        ));
        let db = CountryDatabase::parse_v2fly(&bytes, &stopped()).unwrap();
        for (ip, expected) in [
            ("1.3.0.1", "DE"),
            ("1.2.3.4", "FR"),
            ("::ffff:1.2.3.4", "FR"),
            ("2001:db8::1", "JP"),
        ] {
            assert_eq!(db.lookup(ip.parse().unwrap()), Some(expected));
        }
        assert_eq!(db.lookup("8.8.8.8".parse().unwrap()), None);
    }
    #[test]
    fn protobuf_rejects_conflicts_truncation_overflow_and_cancellation() {
        let mut bytes = country("DE", &[1, 0, 0, 0], 8);
        bytes.extend(country("FR", &[1, 0, 0, 0], 8));
        assert!(CountryDatabase::parse_v2fly(&bytes, &stopped()).is_err());
        for bytes in [&[10, 127][..], &[0][..], &[255; 11][..]] {
            assert!(CountryDatabase::parse_v2fly(bytes, &stopped()).is_err());
        }
        assert!(
            CountryDatabase::parse_v2fly(&country("DE", &[1, 0, 0, 0], 8), &AtomicBool::new(true))
                .is_err()
        );
    }
    #[test]
    fn named_routing_sets_are_not_displayed_as_countries() {
        let mut bytes = country("PRIVATE", &[10, 0, 0, 0], 8);
        bytes.extend(country("DE", &[1, 0, 0, 0], 8));
        let db = CountryDatabase::parse_v2fly(&bytes, &stopped()).unwrap();
        assert_eq!(db.lookup("10.1.2.3".parse().unwrap()), None);
        assert!(normalize_country("ZZ").is_none());
        assert_eq!(normalize_country("de").as_deref(), Some("DE"));
    }
    #[test]
    fn csv_zip_requires_both_families_and_preserves_country_lookup() {
        let csv =
            b"network,continent,country\n1.0.0.0/8,EU,DE\n1.2.0.0/16,EU,FR\n2001:db8::/32,AS,JP\n";
        let db = CountryDatabase::parse_iplocate(&archive(csv), 3, 1, &stopped()).unwrap();
        assert_eq!(db.lookup("1.2.3.4".parse().unwrap()), Some("FR"));
        assert_eq!(db.lookup("2001:db8::1".parse().unwrap()), Some("JP"));
        assert!(CountryDatabase::parse_iplocate(&archive(csv), 4, 1, &stopped()).is_err());
        assert!(
            CountryDatabase::parse_iplocate(
                &archive(b"network,c,x\n1.0.0.0/8,EU,DE\n"),
                1,
                1,
                &stopped()
            )
            .is_err()
        );
    }
    #[test]
    fn zip_invalid_utf8_huge_line_and_cancel_fail_boundedly() {
        assert!(
            CountryDatabase::parse_iplocate(&archive(b"network,c,x\n\xff\n"), 1, 1, &stopped())
                .is_err()
        );
        assert!(
            CountryDatabase::parse_iplocate(&archive(&vec![b'x'; 257]), 1, 1, &stopped()).is_err()
        );
        assert!(
            CountryDatabase::parse_iplocate(
                &archive(b"network,c,x\n"),
                1,
                1,
                &AtomicBool::new(true)
            )
            .is_err()
        );
    }
    #[test]
    fn zip_entry_count_is_bounded_before_the_archive_table_is_allocated() {
        let mut zip = archive(b"network,c,x\n");
        let trailer = zip
            .windows(4)
            .rposition(|part| part == [0x50, 0x4b, 0x05, 0x06])
            .unwrap();
        zip[trailer + 8..trailer + 10].copy_from_slice(&65_000u16.to_le_bytes());
        zip[trailer + 10..trailer + 12].copy_from_slice(&65_000u16.to_le_bytes());
        assert!(validate_zip_entry_limit(&zip).is_err());
        assert!(CountryDatabase::parse_iplocate(&zip, 1, 1, &stopped()).is_err());
    }
    #[test]
    fn zip64_sentinels_and_locator_cannot_bypass_the_small_eocd_count() {
        let zip = archive(b"network,c,x\n");
        let trailer = zip
            .windows(4)
            .rposition(|part| part == [0x50, 0x4b, 0x05, 0x06])
            .unwrap();
        for offset in [12, 16] {
            let mut sentinel = zip.clone();
            sentinel[trailer + offset..trailer + offset + 4]
                .copy_from_slice(&u32::MAX.to_le_bytes());
            assert!(validate_zip_entry_limit(&sentinel).is_err());
        }
        let mut zip64 = zip[..trailer].to_vec();
        let record_start = zip64.len() as u64;
        zip64.extend_from_slice(&[0x50, 0x4b, 0x06, 0x06]);
        zip64.extend_from_slice(&44u64.to_le_bytes());
        zip64.extend_from_slice(&[45, 0, 45, 0]);
        zip64.extend_from_slice(&[0; 8]);
        zip64.extend_from_slice(&65u64.to_le_bytes());
        zip64.extend_from_slice(&65u64.to_le_bytes());
        zip64.extend_from_slice(&[0; 16]);
        zip64.extend_from_slice(&[0x50, 0x4b, 0x06, 0x07]);
        zip64.extend_from_slice(&[0; 4]);
        zip64.extend_from_slice(&record_start.to_le_bytes());
        zip64.extend_from_slice(&1u32.to_le_bytes());
        zip64.extend_from_slice(&zip[trailer..]);
        assert!(validate_zip_entry_limit(&zip64).is_err());
        assert!(CountryDatabase::parse_iplocate(&zip64, 1, 1, &stopped()).is_err());
    }
    #[test]
    fn fake_small_trailer_cannot_hide_an_oversized_earlier_directory() {
        let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
        for index in 0..65 {
            writer
                .start_file(
                    format!("entry-{index}"),
                    zip::write::SimpleFileOptions::default(),
                )
                .unwrap();
        }
        let mut zip = writer.finish().unwrap().into_inner();
        let position = zip.len();
        let mut fake = [0u8; 22];
        fake[..4].copy_from_slice(&[0x50, 0x4b, 0x05, 0x06]);
        fake[8..10].copy_from_slice(&1u16.to_le_bytes());
        fake[10..12].copy_from_slice(&1u16.to_le_bytes());
        fake[16..20].copy_from_slice(&((position - 1) as u32).to_le_bytes());
        zip.extend_from_slice(&fake);
        assert!(validate_zip_entry_limit(&zip).is_err());
        assert!(CountryDatabase::parse_iplocate(&zip, 1, 1, &stopped()).is_err());
    }
    #[test]
    fn paired_sort_has_no_country_index_drift() {
        let mut builder = Builder::default();
        for (ip, code) in [
            ("4.0.0.0", "DE"),
            ("1.0.0.0", "FR"),
            ("3.0.0.0", "JP"),
            ("2.0.0.0", "GB"),
        ] {
            builder.add(ip.parse().unwrap(), 8, code).unwrap();
        }
        let db = builder.finish(&stopped()).unwrap();
        for (ip, expected) in [
            ("1.1.1.1", "FR"),
            ("2.1.1.1", "GB"),
            ("3.1.1.1", "JP"),
            ("4.1.1.1", "DE"),
        ] {
            assert_eq!(db.lookup(ip.parse().unwrap()), Some(expected));
        }
    }
}
