from pathlib import Path
p=Path('Program.cs'); s=p.read_text(encoding='utf-8'); lines=Path('logo.txt').read_text(encoding='utf-8').splitlines()
def esc(x):
    return ''.join(('\\u%04x'%ord(c)) if ord(c)>127 else ('\\\\' if c=='\\' else '\\"' if c=='"' else c) for c in x)
arr=',\n'.join('                "'+esc(x)+'"' for x in lines)
method='''        private static void PrintMark()\n        {\n            string[] logo = new string[]\n            {\n%s\n            };\n\n            for (int i = 0; i < logo.Length; i++)\n            {\n                Push(P_BRIGHT);\n                Say(logo[i]);\n                Pop();\n            }\n        }\n\n'''%arr
start=s.index('        private static void PrintMark()'); end=s.index('        // ---------------- config ----------------',start)
p.write_text(s[:start]+method+s[end:],encoding='utf-8')
