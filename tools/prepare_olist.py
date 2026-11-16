"""
Prepare the Olist Brazilian E-Commerce dataset for the Replayer.

Input  (Kaggle "olist/brazilian-ecommerce", unzip into one folder):
    olist_orders_dataset.csv, olist_customers_dataset.csv,
    olist_order_items_dataset.csv, olist_sellers_dataset.csv
Output:
    <out>/chunks/00000.jsonl, 00001.jsonl, ...   (500 orders per file, oldest first)

Each output line = one order with its shipment timeline:
    {"orderId","carrier","trackingCode","customerState","sellerState",
     "estimatedDelivery","events":[{"status":"Created","at":"...Z"}, ...]}

What is REAL vs SYNTHETIC (be honest in the report):
    real      : order ids, purchase / carrier pickup / customer delivery timestamps,
                promised (estimated) delivery date, customer & seller state
    synthetic : carrier assignment (ghn/ghtk/jt), tracking codes, InTransit and
                OutForDelivery times, a FailedAttempt before late deliveries

Usage:
    python tools/prepare_olist.py --input data/olist --output data/prepared
    python tools/prepare_olist.py --input tools/sample-data --output data/sample-prepared
Only the Python standard library is used.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path

# Olist timestamps are Brazilian local time (São Paulo, UTC-3; DST ignored for simplicity).
OLIST_TZ = timezone(timedelta(hours=-3))
TS_FORMAT = "%Y-%m-%d %H:%M:%S"


def parse_ts(value: str) -> datetime | None:
    """'2017-10-02 10:56:33' (Brazil time) → aware UTC datetime, or None when empty."""
    if not value:
        return None
    return datetime.strptime(value, TS_FORMAT).replace(tzinfo=OLIST_TZ).astimezone(timezone.utc)


def iso(t: datetime) -> str:
    return t.strftime("%Y-%m-%dT%H:%M:%SZ")


def assign_carrier(order_id: str) -> tuple[str, str]:
    """Deterministic carrier + tracking code from the order id (same input → same output)."""
    h = hashlib.md5(order_id.encode()).hexdigest()
    bucket = int(h[:2], 16) % 100
    if bucket < 45:
        return "ghn", "GHN" + h[:8].upper()
    if bucket < 80:
        return "ghtk", f"S1.A1.{int(h[:8], 16)}"
    return "jt", "JT" + str(int(h[8:20], 16))[:10]


def build_timeline(o: dict) -> list[tuple[str, datetime]] | None:
    """Turn one Olist order row into a list of (status, time). None = order skipped."""
    status = o["order_status"]
    purchase = parse_ts(o["order_purchase_timestamp"])
    picked = parse_ts(o["order_delivered_carrier_date"])
    delivered = parse_ts(o["order_delivered_customer_date"])
    estimate = o["order_estimated_delivery_date"][:10]
    if purchase is None:
        return None
    if status == "delivered" and delivered and (picked is None or not purchase < picked < delivered):
        # Real anomaly: pickup missing or recorded after delivery → keep the real delivery,
        # place a synthetic pickup one third of the way between purchase and delivery.
        picked = purchase + (delivered - purchase) / 3

    events: list[tuple[str, datetime]] = [("Created", purchase)]

    if status == "canceled":
        events.append(("Cancelled", purchase + timedelta(hours=2)))  # synthetic time
    elif status == "shipped" and picked:
        events += [("PickedUp", picked), ("InTransit", picked + timedelta(days=1))]
    elif status == "delivered" and picked and delivered:
        transit = picked + (delivered - picked) / 2           # synthetic hub scan
        late = delivered.date().isoformat() > estimate
        events += [("PickedUp", picked), ("InTransit", transit)]
        first_try = delivered - timedelta(days=1, hours=6)
        if late and first_try > transit:
            # synthetic failed attempt one day before a late delivery
            events += [("OutForDelivery", first_try), ("FailedAttempt", delivered - timedelta(days=1))]
        events += [("OutForDelivery", delivered - timedelta(hours=6)), ("Delivered", delivered)]
    else:
        return None  # created / approved / invoiced / processing / unavailable: no shipment yet

    # Real data has anomalies (e.g. pickup recorded after delivery): keep a strictly increasing timeline.
    cleaned: list[tuple[str, datetime]] = []
    for name, t in events:
        if not cleaned or t > cleaned[-1][1]:
            cleaned.append((name, t))
    return cleaned if len(cleaned) >= 2 else None


def read_csv(path: Path) -> list[dict]:
    with path.open(encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--input", required=True, type=Path, help="folder with the Olist CSV files")
    ap.add_argument("--output", required=True, type=Path, help="output folder (chunks/ is created inside)")
    ap.add_argument("--chunk-size", type=int, default=500, help="orders per chunk (must match CHUNK_SIZE)")
    ap.add_argument("--limit", type=int, default=0, help="only keep the first N orders (0 = all)")
    args = ap.parse_args()

    orders = read_csv(args.input / "olist_orders_dataset.csv")
    customer_state = {c["customer_id"]: c["customer_state"] for c in read_csv(args.input / "olist_customers_dataset.csv")}
    seller_state = {s["seller_id"]: s["seller_state"] for s in read_csv(args.input / "olist_sellers_dataset.csv")}
    first_seller: dict[str, str] = {}
    for item in read_csv(args.input / "olist_order_items_dataset.csv"):
        first_seller.setdefault(item["order_id"], item["seller_id"])  # orders may have several items

    orders.sort(key=lambda o: o["order_purchase_timestamp"])  # replay in historical order

    records, stats = [], Counter()
    for o in orders:
        timeline = build_timeline(o)
        if timeline is None:
            stats["skipped"] += 1
            continue
        carrier, tracking = assign_carrier(o["order_id"])
        records.append({
            "orderId": o["order_id"],
            "carrier": carrier,
            "trackingCode": tracking,
            "customerState": customer_state.get(o["customer_id"]),
            "sellerState": seller_state.get(first_seller.get(o["order_id"], "")),
            "estimatedDelivery": o["order_estimated_delivery_date"][:10] or None,
            "events": [{"status": s, "at": iso(t)} for s, t in timeline],
        })
        stats[f"carrier:{carrier}"] += 1
        stats[f"final:{timeline[-1][0]}"] += 1
        if any(s == "FailedAttempt" for s, _ in timeline):
            stats["late_with_failed_attempt"] += 1
        if args.limit and len(records) >= args.limit:
            break

    out_dir = args.output / "chunks"
    out_dir.mkdir(parents=True, exist_ok=True)
    for old in out_dir.glob("*.jsonl"):  # avoid mixing with a previous run
        old.unlink()
    for i in range(0, len(records), args.chunk_size):
        chunk = records[i:i + args.chunk_size]
        path = out_dir / f"{i // args.chunk_size:05d}.jsonl"
        path.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in chunk), encoding="utf-8")

    events = sum(len(r["events"]) for r in records)
    print(f"orders kept : {len(records)}  (skipped {stats['skipped']})")
    print(f"events      : {events}")
    print(f"chunks      : {(len(records) + args.chunk_size - 1) // args.chunk_size} → {out_dir}")
    for key in sorted(k for k in stats if k != "skipped"):
        print(f"  {key:28} {stats[key]}")


if __name__ == "__main__":
    main()
