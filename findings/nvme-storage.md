# NVMe storage on the VENTUNO Q

## Checked 2026-10-02, the Silicon Power 256 GB drive will not fit

Asked about this drive:

```
Silicon Power 256GB NVMe M.2 PCIe Gen3x4 SSD
model SP256GBP34A60M28
Amazon ASIN B07ZGK3K4V
M.2 2280, M key, DRAM-less
read up to 2200 MB/s, write up to 1600 MB/s
```

Do not buy it for this board. It is 2280 and the VENTUNO Q M.2 slot is 2230 only. The Arduino user manual is explicit: the mounting standoff is positioned for 2230 modules, and 2242, 2260, and 2280 drives cannot be secured with the retention screw. The slot is electrically M key PCIe Gen4 NVMe, so the drive would work if it fit, but it does not fit.

What to buy instead: an M.2 **2230** M key NVMe drive. That is the Steam Deck and Surface size. Common parts in that size are the WD SN740, WD SN770M, Sabrent Rocket 2230, Corsair MP600 Mini, and Inland TN446. Capacity 256 GB to 1 TB. Prefer a DRAM-less drive with HMB support, which all of those have, and prefer one with a decent sustained write rating because we will be writing a cache database continuously.

## Checked 2026-10-02, the Fanxiang S630 500 GB is a reasonable buy

Asked about this listing:

```
Fanxiang S630 500GB NVMe SSD
Amazon ASIN B0DZX34SZN
M.2 2230, M key, PCIe Gen4 x4 NVMe
read up to 4850 MB/s
write up to 1900 MB/s
endurance 160 TBW
five-year limited warranty or 160 TBW
graphene heat spreader label
```

This one is mechanically and electrically compatible with the VENTUNO Q. It is the named Fanxiang S630 rather than a seller-variable OEM part. It is 2230, M key, NVMe, and PCIe Gen4 x4. The board exposes the same Gen4 x4 link and already has the Linux NVMe driver.

The 'Microsoft Surface compatible' part of the title does not matter. That is just search text. The useful facts are the 2230 dimensions, M key edge connector, NVMe protocol, and Gen4 x4 interface.

160 TBW is enough for Kare. Spread evenly over the five-year warranty, it is roughly 88 GB of writes per day. Kare should not come close to that once cache size, retention, log rotation, and database maintenance are bounded. PostgreSQL, model downloads, indexes, benchmark results, and logs are a normal client SSD workload at this scale.

500 GB is also a much better capacity than the 256 GB Micron listing. A Phi-4-mini model consumes about 5 GB before any alternate quantizations, Qwen models, compiled QNN contexts, database backups, or repository indexes. 500 GB gives us enough room to keep several model candidates and collect real cache data before storage policy becomes the experiment.

The claimed 4850 MB/s read speed is near the practical ceiling for this class of drive and more than the board needs. The measured eMMC read rate is 294 MB/s. Even if the S630 reaches only half its advertised speed on the board, cold model reads should still be roughly eight times faster than eMMC. It will not improve token generation after the model is resident in memory.

The caveat is component disclosure. Fanxiang says '3D NAND' but does not identify TLC versus QLC, the NAND supplier, the controller, DRAM, or HMB support for this S630 revision. Search results that name a Maxio controller or YMTC TLC appear to be inference from other Fanxiang drives, not a teardown or manufacturer specification for this exact S630. Do not write those claims into the plan as facts. Budget SSD vendors can change internal components while keeping the product name.

That uncertainty does not make it a bad buy at the right price. The named model, explicit 160 TBW rating, five-year limited warranty, double capacity, and Amazon return path make it a lower-risk purchase than the generic OEMGenuine Micron listing. WD, Sabrent, and Corsair remain easier to trust if they are close in price. If the Fanxiang is substantially cheaper, I would use it.

When it arrives:

1. Record the controller, firmware, namespace, temperature sensor, power states, and SMART counters with `nvme id-ctrl`, `nvme id-ns`, and `nvme smart-log`.
2. Run a destructive full-capacity write and verification before putting Kare data on it. Fake capacity is unlikely from Amazon, but storage gets tested before it gets trusted.
3. Measure sequential read, sequential write, 4K random read and write, fsync latency, temperature, and throttling.
4. Put PostgreSQL checksums and encrypted backups in place. The SSD is replaceable storage, not a backup.

Verdict:

```
will it fit?                 yes
will Linux recognize it?    expected yes, not measured with this exact drive
capacity                     good for the first several Kare stages
endurance                    adequate at 160 TBW
unknown                      controller and exact NAND
would I buy this ASIN?       yes, if meaningfully cheaper than WD, Sabrent, or Corsair
preference over Micron OEM   yes, this listing is less ambiguous and twice the capacity
```

Listing: https://www.amazon.com/dp/B0DZX34SZN

Manufacturer page: https://www.fanxiangssd.com/products/internal-solid-state-drive-2230-nvme-ssd-pcie-4-0-fanxiang-s700

## Checked 2026-10-02, the second link is OSCOO ON1000T 512 GB, not Fanxiang

The ASIN in the second link is `B0FH4P66MZ`. It resolves to an **OSCOO 512 GB M.2 2230 NVMe Gen4x4** listing, commonly identified as the OSCOO ON1000T. It is not another Fanxiang S630 listing.

The advertised specifications are:

```
OSCOO ON1000T 512GB
M.2 2230, single-sided, M key
PCIe Gen4 x4 NVMe
read up to 5200 MB/s
write up to 4000 MB/s
claimed endurance 325 TBW
three-year limited warranty
3D NAND, graphene heat spreader
```

This is also the correct physical and electrical fit for the VENTUNO Q. The board exposes an empty PCIe Gen4 x4 root port for the M.2 slot, and the Ubuntu image already has the NVMe host driver. The drive should enumerate without an adapter or kernel work, but this exact model has not been installed on the board yet.

The OSCOO has two practical advantages over the Fanxiang S630: 512 GB instead of 500 GB, and a claimed 325 TBW instead of 160 TBW. The extra endurance is useful for a cache database and logs, although both ratings are sufficient for the planned workload. The advertised 5200/4000 MB/s numbers will not make inference faster. They only matter for model loading, database access, indexing, and backup work.

The OSCOO listing has the same value-tier caveat as the Fanxiang listing. The manufacturer page identifies 3D NAND but does not clearly identify the controller, NAND supplier, TLC versus QLC, DRAM, or HMB behavior for this exact 512 GB revision. Search results that name a specific controller or TLC NAND are not enough evidence. Budget SSD vendors can change components while keeping the same product name.

The public warranty information is also less clean than the product title. OSCOO documentation points to a three-year limited warranty, while some indexed marketplace pages mix specifications from other OSCOO capacities or models. Save the Amazon invoice, photograph the label and serial number, and verify the actual model in `nvme id-ctrl` after delivery.

Comparison:

```
                         Fanxiang S630       OSCOO ON1000T
capacity                 500 GB              512 GB
form factor              M.2 2230            M.2 2230
interface                PCIe Gen4 x4        PCIe Gen4 x4
advertised read          4850 MB/s           5200 MB/s
advertised write         1900 MB/s           4000 MB/s
claimed endurance        160 TBW             325 TBW
claimed warranty         5 years             3 years
controller and NAND      unknown             unknown
board-tested             no                  no
```

The write-speed gap is probably burst-cache behavior, not a useful promise for a database. Both are compact, DRAM-less-class consumer drives until proven otherwise, and both may slow substantially after their pseudo-SLC cache fills. Kare will write small cache records, logs, and database pages rather than one 500 GB sequential file, so the advertised sequential write maximum is not the deciding number.

My choice:

- Buy the OSCOO if it is close in price to the Fanxiang. It has twice the advertised endurance and slightly more usable capacity.
- Buy the Fanxiang if it is materially cheaper or if the seller and return policy are better. It is already a reasonable fit for Kare.
- Buy neither over a named WD, Sabrent, Corsair, or Micron retail/OEM part with a verifiable model number if the price difference is small.

The new link is the better value on paper. It is not the better-proven drive.

When it arrives, run the same checks listed for the Fanxiang: `nvme id-ctrl`, `nvme id-ns`, `nvme smart-log`, a full-capacity write and verification, sequential and random I/O tests, fsync latency, temperature, and throttling. Put the database and cache on it only after those checks pass.

The OSCOO was ordered on 2026-10-02. It is expected in about 18 hours. Do not move Kare data until the identity, full capacity, SMART data, temperature, and sustained write behavior are measured.

Listing: https://www.amazon.com/gp/product/B0FH4P66MZ

Manufacturer page: https://www.oscoo.com/product/m-2-nvme-pcie-gen4-04-2230-ssd/

## Checked 2026-10-02, the OEMGenuine Micron 256 GB drive should fit, but the listing is too vague

Asked about this listing:

```
OEMGenuine OEM Micron 256GB M.2 PCI-e Gen4 NVMe SSD
Amazon ASIN B0H125X15W
M.2 2230, M key, PCIe Gen4 x4
seller warranty: 1 year
exact Micron series and part number: not listed
condition: not clear enough
```

This one is mechanically and electrically compatible with the VENTUNO Q on paper. It is 2230, M key, NVMe, and PCIe Gen4 x4. The board exposes an empty Gen4 x4 root port for the slot and already has the Linux NVMe driver. It should plug in, enumerate, and run without an adapter or kernel work.

I would not buy this exact listing for Kare unless the seller confirms two things in writing:

1. The complete Micron part number printed on the label, usually something like `MTFDKBK256TFK`, `MTFDKBK256TGE`, or `MTFDKBK256TGW`.
2. Whether the drive is new OEM stock or a pull from another machine, with a SMART report if it is a pull.

The ASIN identifies a generic Micron 256 GB 2230 drive, not a Micron product series. Micron has shipped several drives matching that description:

```
Micron 2450   MTFDKBK256TFK   Gen4 x4   176-layer TLC
Micron 2550   MTFDKBK256TGE   Gen4 x4   232-layer TLC
Micron 2650   MTFDKBK256TGW   Gen4 x4   newer TLC generation
```

Those are not interchangeable when we are deciding whether to put a write-heavy cache database on one. Performance, endurance, firmware, security features, and power behavior vary by series. Indexed copies of the listing advertise a one-year seller warranty. Do not assume Micron will provide direct warranty service for an OEM drive. The reseller may be the only party standing behind it.

If the delivered drive is a Micron 2550 256 GB, the known part is a good technical fit: 2230, TLC, HMB, roughly 4.5 GB/s sequential read, roughly 2.0 GB/s sequential write, and 150 TBW. A Micron 2450 is older and slower but still fine for this board. A Micron 2650 is newer and also fine. The problem is that this listing does not promise any one of them.

256 GB is enough for the first Kare build. It leaves room for a handful of 1B to 4B quantized models, PostgreSQL, indexes, cache entries, benchmark results, and logs. It is not much room once we keep several 5 GB models or start retaining large repository indexes. I would buy 512 GB or 1 TB if the price difference is modest.

Verdict:

```
will it fit?                 yes
will Linux recognize it?    expected yes, not measured with this exact drive
will it beat the eMMC?       expected yes, by a wide margin
would I buy this ASIN?       only after the seller confirms the exact part and condition
preferred purchase          named 2230 model, 512 GB or 1 TB, with a real warranty
```

Listing: https://www.amazon.com/dp/B0H125X15W

Board manual: https://docs.arduino.cc/tutorials/ventuno-q/user-manual/

Micron 2550 product brief: https://www.mouser.com/datasheet/2/671/2550_nvme_ssd_product_brief-3460051.pdf

## Measured 2026-10-02 on the board, what the slot actually is

Confirmed on the device rather than taken from a spec sheet.

```
lspci
0000:00:00.0 PCI bridge: Qualcomm Technologies, Inc Device 0115
0000:01:00.0 PCI bridge: Pericom Semiconductor Device b304
0000:02:01.0 PCI bridge: Pericom Semiconductor Device b304
0000:02:02.0 PCI bridge: Pericom Semiconductor Device b304
0000:03:00.0 Network controller: Qualcomm QCNFA765 Wireless
0000:04:00.0 USB controller: TI TUSB73x0 SuperSpeed USB 3.0 xHCI
0001:00:00.0 PCI bridge: Qualcomm Technologies, Inc Device 0115
```

There are two PCIe root complexes. The first one is fully consumed on board: a Pericom switch fans it out to the WiFi card and the USB 3.0 controller. The second one, `0001:00:00.0`, has nothing behind it. That is the M.2 slot.

```
/sys/bus/pci/devices/0001:00:00.0/max_link_speed   16.0 GT/s PCIe
/sys/bus/pci/devices/0001:00:00.0/max_link_width   4
```

16 GT/s by four lanes is PCIe Gen4 x4. The first root complex is only x2, which is another sign that the second one is the expansion slot and not something internal.

The driver is already present, so this is plug and go with no kernel work:

```
modinfo nvme
filename: /lib/modules/6.8.0-1084-qcom/kernel/drivers/nvme/host/nvme.ko.zst
description: NVMe host PCIe transport driver
```

## Measured 2026-10-02, what we expected to gain

Current storage is eMMC, `/dev/mmcblk0`, 59.3 GB with about 34 GB free, ext4 on `/dev/mmcblk0p71`. Sequential read measured with caches dropped:

```
dd if=model.onnx.data of=/dev/null bs=1M count=600
629145600 bytes (629 MB, 600 MiB) copied, 2.13674 s, 294 MB/s
```

294 MB/s. That is a good eMMC number and it is still an order of magnitude below any modern NVMe drive.

Expected gains, stated as expectations and not measurements, since no drive is installed:

Model load. A 2230 Gen4 drive will do 3 to 5 GB/s sequential on this link, call it 10x the eMMC. The Phi-4-mini INT4 weights are 4.86 GB, so a cold load goes from roughly 17 seconds to under 2 seconds. That only matters on service start and after a model swap, because once loaded the weights sit in page cache. With 15 GB of RAM and a 5 GB model there is room to keep it resident. So this is a startup and model-switching win, not an inference win.

Cache database. This is the real reason to add the drive. eMMC random write is poor and write amplification on small synchronous commits is brutal. A cache and context store doing frequent small writes with fsync is exactly the workload eMMC is worst at. NVMe is typically 20x to 50x better on 4K random write IOPS. It also removes a correctness risk: the board boots from this eMMC and there is no swap, so grinding the boot device with a write-heavy database is asking for trouble.

eMMC lifetime. Writing a cache continuously to the boot eMMC will wear it out and when it dies the board does not boot. Moving writes to a replaceable drive is worth doing on reliability grounds alone, separate from speed.

Embeddings and vector search. If we add pgvector or any local index, random read latency dominates. NVMe is roughly 100 microseconds against eMMC in the high hundreds, and the gap widens under concurrency because NVMe has real queue depth.

What it will not fix. Inference is CPU and NPU bound. Prefill is currently about 15 ms per prompt token on CPU and storage has nothing to do with that. Do not expect the NVMe to make generation faster.

## Measured 2026-10-06, the OSCOO drive is installed

The board now sees the installed drive:

```
/dev/nvme0n1 476.9G OSCOO PCIe 512GB
controller                 MAXIO MAP1602, DRAM-less
firmware                   SN025696
logical block size         512 bytes
physical block size        512 bytes
partition table            none
filesystem                 none
mount                      none
kernel driver              nvme
```

The PCIe root port can run at Gen4 x4, but the active link is downgraded:

```
LnkCap:  Speed 16GT/s, Width x4
LnkSta:  Speed 16GT/s, Width x1 (downgraded)
```

The first read-only test transferred 1 GiB with direct I/O at 1.6 GB/s. That is about 5.4 times the earlier 294 MB/s eMMC read, however it is not a full Gen4 x4 result. The x1 negotiation needs a separate hardware and firmware investigation before we claim the slot is operating normally.

`nvme-cli` is not installed on the board yet. The first health pass used sysfs, `lsblk`, `lspci`, and a privileged direct read. No write test has been run, and the drive has not been formatted.

## NVMe rollout plan

The first migration is complete:

1. The drive was partitioned as GPT and formatted as ext4 with `noatime`, then mounted at `/var/lib/kare`.
2. The GenieX model data moved to `/var/lib/kare/models/geniex`.
3. The GenieX Linux ARM64 runtime moved to `/var/lib/kare/runtimes/geniex`.
4. The `kare-geniex.service` unit now uses `GENIEX_DATADIR=/var/lib/kare/models/geniex`.
5. Kare and GenieX restarted successfully, and both `/health` and `/v1/models` passed.
6. Existing Kare and Azure command logs, GenieX cache data, and the old cloud catalog backup were deleted instead of copied.

The service checkout, published Kare releases, protected configuration, and systemd user units remain on eMMC. No persistent logs, response-cache data, or backups were added to the NVMe layout. The bounded response cache remains process-local and starts empty after restart.

The remaining storage work is narrow:

1. Install `nvme-cli` and record SMART data, temperature, percentage used, and media errors.
2. Investigate the Gen4 x1 negotiation through firmware, device tree, kernel, and physical seating checks.
3. Measure cold model load, warm model load, and sustained temperature after the move.
4. Add storage health metadata to the dashboard without exposing serial numbers, prompts, source code, or credentials.

The first useful layout is:

```
/var/lib/kare/
  models/geniex/
  runtimes/geniex/
```

Keep model directories checksummed and versioned. Do not store prompts, source code, credentials, logs, cache entries, backups, or copied Copilot settings in a general-purpose storage directory just because it is large.

The drive is a storage and startup improvement, not an inference accelerator. GenieX still owns token latency. NVMe should reduce cold model load time and move the native runtime and model data away from the boot eMMC.
