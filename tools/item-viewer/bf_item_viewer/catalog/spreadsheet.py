"""Streaming reader for the client's Excel-2003 XML (SpreadsheetML) workbooks.

``ShopItem2.xml`` is 45 MB (kr-812) to 100 MB (kr-1256), so sheets are streamed with iterparse
and only the requested sheets are materialised. Sparse cells (``ss:Index``) are honoured.
"""

from __future__ import annotations

import io
import xml.etree.ElementTree as ET

NS = "{urn:schemas-microsoft-com:office:spreadsheet}"
MAX_ROWS = 2_000_000


def read_sheets(data: bytes, wanted: set[str] | None = None, header_row: int = 1) -> dict[str, list[dict]]:
    """Return ``{sheet: [row dict]}`` using row ``header_row`` (1-based) as column names."""
    out: dict[str, list[dict]] = {}
    current: str | None = None
    rows: list[list[str]] = []
    row: list[str] = []
    col = 0
    for event, element in ET.iterparse(io.BytesIO(data), events=("start", "end")):
        tag = element.tag
        if event == "start":
            if tag == NS + "Worksheet":
                current = element.get(NS + "Name")
                rows = []
            elif tag == NS + "Row" and current is not None:
                row = []
                col = 0
            elif tag == NS + "Cell" and current is not None:
                index = element.get(NS + "Index")
                if index:
                    col = int(index) - 1
            continue
        if tag == NS + "Cell" and current is not None:
            text = "".join(element.itertext()).strip()
            while len(row) < col:
                row.append("")
            row.append(text)
            col += 1
            element.clear()
        elif tag == NS + "Row" and current is not None:
            if wanted is None or current in wanted:
                if len(rows) >= MAX_ROWS:
                    raise ValueError(f"sheet {current} exceeds {MAX_ROWS} rows")
                rows.append(row)
            element.clear()
        elif tag == NS + "Worksheet":
            if current is not None and (wanted is None or current in wanted) and len(rows) >= header_row:
                width = max(len(r) for r in rows)
                header = rows[header_row - 1] + [""] * (width - len(rows[header_row - 1]))
                names = [h if h else f"col{i}" for i, h in enumerate(header)]
                out[current] = [dict(zip(names, r + [""] * (len(names) - len(r)))) for r in rows[header_row:]
                                if any(r)]
            current = None
            element.clear()
    return out


def read_raw(data: bytes, sheet: str) -> list[list[str]]:
    """All rows of one sheet as lists, for transposed ``HorzTable`` sheets (fields are rows)."""
    rows = read_sheets(data, {sheet}, header_row=1).get(sheet, [])
    if not rows:
        return []
    names = list(rows[0].keys())
    return [names] + [[r[n] for n in names] for r in rows]
