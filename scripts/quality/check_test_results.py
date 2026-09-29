"""Fail closed on empty, failed, or skipped integration suites."""
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
counters = root.find(".//{*}Counters")
assert counters is not None, "No test counters"
total = int(counters.attrib["total"])
passed = int(counters.attrib["passed"])
assert total > 0 and total == passed, f"Expected all tests passing: {counters.attrib}"
print(f"{passed} passed, zero failed/skipped")
