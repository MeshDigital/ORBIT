import sys
import xml.etree.ElementTree as ET

try:
    with open(sys.argv[1], 'r', encoding='utf-8') as f:
        content = f.read()
    ET.fromstring(content)
    print("XML is valid")
except ET.ParseError as e:
    print(f"XML Trace: {e}")
    line, column = e.position
    lines = content.split('\n')
    print(f"Error at line {line}, column {column}")
    if line > 0 and line <= len(lines):
        print(f"Line content: {lines[line-1]}")
except Exception as e:
    print(f"Error: {e}")
