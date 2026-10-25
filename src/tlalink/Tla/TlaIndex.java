package pcal;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.PrintStream;
import java.lang.reflect.Field;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Vector;

import pcal.AST;
import pcal.PcalCharReader;
import pcal.PcalParams;
import pcal.ParseAlgorithm;
import pcal.Region;
import pcal.TLAtoPCalMapping;
import tla2sany.drivers.SANY;
import tla2sany.modanalyzer.SpecObj;
import tla2sany.semantic.OpDefNode;
import tla2sany.st.Location;
import util.SimpleFilenameToStream;
import util.FilenameToStream.TLAFile;

public final class TlaIndex {
    private final List<String> entries = new ArrayList<>();
    private final IdentityHashMap<AST, Boolean> visited = new IdentityHashMap<>();

    public static void main(String[] args) throws Exception {
        PrintStream output = System.out;
        ByteArrayOutputStream messages = new ByteArrayOutputStream();
        try (PrintStream capture = new PrintStream(messages, true, "UTF-8")) {
            System.setOut(capture);
            try {
                TlaIndex index = new TlaIndex();
                index.parse(Path.of(args[0]), args);
                output.println("[" + String.join(",", index.entries) + "]");
            } catch (Exception error) {
                System.err.println(messages.toString("UTF-8"));
                System.err.println(error.getClass().getSimpleName() + ": " + error.getMessage());
                System.exit(1);
            } finally {
                System.setOut(output);
            }
        }
    }

    private void parse(Path path, String[] args) throws Exception {
        TrackingResolver resolver = new TrackingResolver();
        SpecObj specification = new SpecObj(path.toString(), resolver);
        int status = SANY.frontEndMain(specification, path.toString(), System.out);
        if (status != 0 || specification.getErrorLevel() != 0
                || specification.getRootModule() == null) {
            throw new IllegalArgumentException("SANY rejected " + path);
        }
        String module = specification.getRootModule().getName().toString();
        for (tla2sany.semantic.ModuleNode loaded : specification.getExternalModuleTable().getModuleNodes()) {
            if (loaded.getAssumptions().length > 0) {
                entries.add("{\"kind\":\"assumptions\",\"module\":" + quote(loaded.getName().toString())
                    + ",\"count\":" + loaded.getAssumptions().length + "}");
            }
        }
        for (OpDefNode operator : specification.getRootModule().getOpDefs()) {
            Location place = operator.getLocation();
            if (!place.source().equals(module)) {
                continue;
            }
            entries.add(entry("operator", operator.getName().toString(), module,
                place.beginLine(), place.beginColumn(), place.endLine(), place.endColumn()));
            entries.add("{\"kind\":\"operator-level\",\"name\":" + quote(operator.getName().toString())
                + ",\"level\":" + operator.getLevel() + "}");
        }
        if (args.length > 1) {
            parsePlusCal(path, args, module);
        }
        for (SourceHash source : resolver.sources.values()) {
            if (!source.digest.equals(digest(source.path))) {
                throw new IllegalArgumentException("Module changed while parsing: " + source.path);
            }
            entries.add("{\"kind\":\"dependency\",\"path\":" + quote(source.key)
                + ",\"sha256\":" + quote(source.digest) + "}");
        }
    }

    private void parsePlusCal(Path path, String[] args, String module) throws Exception {
        Vector<String> lines = new Vector<>(Files.readAllLines(path, StandardCharsets.UTF_8));
        if (!lines.isEmpty()) {
            lines.set(0, lines.get(0).replace("\uFEFF", ""));
        }
        AST algorithm = algorithm(lines, args, true);
        visit(algorithm, module);

        int begin = -1;
        int end = -1;
        for (int i = 0; i < lines.size(); i++) {
            if (lines.get(i).stripLeading().startsWith("\\* BEGIN TRANSLATION")) {
                if (begin != -1) {
                    throw new IllegalArgumentException("Multiple PlusCal translation blocks: " + path);
                }
                begin = i;
            }
            if (lines.get(i).stripLeading().startsWith("\\* END TRANSLATION")) {
                if (end != -1) {
                    throw new IllegalArgumentException("Multiple PlusCal translation blocks: " + path);
                }
                end = i;
            }
        }
        String state = "missing";
        String detail = "";
        int diagnosticLine = begin >= 0 ? begin + 1 : Integer.parseInt(args[1]) + 1;
        if (begin >= 0 && end > begin) {
            PCalTLAGenerator generator = new PCalTLAGenerator(algorithm(lines, args, false));
            try {
                generator.removeNameConflicts();
                List<String> translation = generator.translate();
                List<String> existing = lines.subList(begin + 1, end);
                state = translation.equals(existing) ? "current" : "stale";
                if (state.equals("stale")) {
                    int i = 0;
                    while (i < translation.size() && i < existing.size()
                            && translation.get(i).equals(existing.get(i))) {
                        i++;
                    }
                    diagnosticLine = begin + 2 + i;
                    detail = " Expected: " + (i < translation.size() ? translation.get(i) : "<end>")
                        + "; found: " + (i < existing.size() ? existing.get(i) : "<end>");
                }
            } catch (pcal.exception.RemoveNameConflictsException error) {
                throw new IllegalArgumentException(error.getMessage(), error);
            }
        }
        entries.add("{\"kind\":\"translation\",\"state\":" + quote(state)
            + ",\"detail\":" + quote(detail) + ",\"line\":" + diagnosticLine + "}");
    }

    private AST algorithm(Vector<String> original, String[] args, boolean forLabels) throws Exception {
        Vector<String> lines = trans.removeTabs(original);
        int line = Integer.parseInt(args[1]);
        Vector<String> prefix = new Vector<>();
        prefix.add(original.get(line).substring(0, Integer.parseInt(args[2])));
        int column = trans.removeTabs(prefix).get(0).length();
        PcalParams.resetParams();
        readOptions(lines);
        if (PcalParams.WriteASTFlag || PcalParams.tlcTranslation()) {
            throw new IllegalArgumentException(
                "PlusCal file-writing/TLC translation options are not supported by TlaLink");
        }
        if (forLabels) {
            PcalParams.LabelFlag = true;
        }
        PcalParams.tlaPcalMapping = new TLAtoPCalMapping();
        PcalParams.tlaPcalMapping.algLine = line;
        PcalParams.tlaPcalMapping.algColumn = column;
        try {
            ParseAlgorithm.uncomment(lines, line, column);
            PcalCharReader reader = new PcalCharReader(lines, line, column, lines.size(), 0);
            return ParseAlgorithm.getAlgorithm(reader, Boolean.parseBoolean(args[3]));
        } catch (pcal.exception.ParseAlgorithmException error) {
            throw new IllegalArgumentException(error.getMessage(), error);
        }
    }

    private void readOptions(Vector<String> lines) throws Exception {
        IntPair position = new IntPair(0, 0);
        while (true) {
            try {
                ParseAlgorithm.FindToken("PlusCal", lines, position, "");
            } catch (pcal.exception.ParseAlgorithmException notFound) {
                return;
            }
            String rest = ParseAlgorithm.GotoNextNonSpace(lines, position).substring(position.two);
            if (rest.startsWith("options") && ParseAlgorithm.NextNonIdChar(rest, 0) == 7) {
                PcalParams.optionsInFile = true;
                if (ParseAlgorithm.ProcessOptions(lines, position) != trans.STATUS_OK) {
                    throw new IllegalArgumentException("Invalid or unsupported PlusCal options");
                }
                return;
            }
        }
    }

    private static String digest(Path path) throws IOException, NoSuchAlgorithmException {
        byte[] bytes = MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(path));
        StringBuilder result = new StringBuilder();
        for (byte value : bytes) {
            result.append(String.format("%02x", value & 0xff));
        }
        return result.toString();
    }

    private static final class SourceHash {
        final Path path;
        final String key;
        final String digest;

        SourceHash(TLAFile file) throws IOException, NoSuchAlgorithmException {
            path = file.toPath().toRealPath();
            key = file.hasLibraryPath() && "jar".equals(file.getLibraryPath().getScheme())
                ? "library:" + file.getLibraryPath() + "!" + file.getName()
                : path.toString();
            digest = digest(path);
        }
    }

    private static final class TrackingResolver extends SimpleFilenameToStream {
        final Map<String, SourceHash> sources = new LinkedHashMap<>();

        @Override
        public TLAFile resolve(String name, boolean isModule) {
            TLAFile file = super.resolve(name, isModule);
            if (file.isFile()) {
                try {
                    SourceHash source = new SourceHash(file);
                    SourceHash previous = sources.putIfAbsent(source.key, source);
                    if (previous != null && !previous.digest.equals(source.digest)) {
                        throw new IllegalArgumentException("Module changed while resolving: " + file);
                    }
                } catch (IOException | NoSuchAlgorithmException error) {
                    throw new IllegalArgumentException("Cannot fingerprint module " + file, error);
                }
            }
            return file;
        }
    }

    private void visit(AST node, String scope) throws Exception {
        if (visited.put(node, Boolean.TRUE) != null) {
            return;
        }
        String type = node.getClass().getSimpleName();
        if (type.equals("Process") || type.equals("Procedure")
                || type.equals("Uniprocess") || type.equals("Multiprocess")) {
            scope += "." + node.getClass().getField("name").get(node);
        }
        if (node instanceof AST.LabeledStmt) {
            addLabel((AST.LabeledStmt) node, scope);
        }
        for (Field field : node.getClass().getFields()) {
            if (java.lang.reflect.Modifier.isStatic(field.getModifiers())) {
                continue;
            }
            visitValue(field.get(node), scope);
        }
    }

    private void visitValue(Object value, String scope) throws Exception {
        if (value instanceof AST) {
            visit((AST) value, scope);
        } else if (value instanceof Vector<?>) {
            for (Object item : (Vector<?>) value) {
                visitValue(item, scope);
            }
        }
    }

    private void addLabel(AST.LabeledStmt label, String scope) {
        if (ParseAlgorithm.addedLabels.contains(label.label)) {
            return;
        }
        Region origin = label.getOrigin();
        if (origin == null) {
            throw new IllegalArgumentException("No source extent for PlusCal label " + label.label);
        }
        int line = label.lblLocation == null
            ? origin.getBegin().getLine() : label.lblLocation.getLine();
        int column = label.lblLocation == null
            ? origin.getBegin().getColumn() : label.lblLocation.getColumn();
        entries.add(entry("label", label.label, scope, line + 1, column + 1,
            origin.getEnd().getLine() + 1, origin.getEnd().getColumn()));
    }

    private static String entry(String kind, String name, String scope,
            int line, int column, int endLine, int endColumn) {
        return "{\"kind\":" + quote(kind) + ",\"name\":" + quote(name)
            + ",\"scope\":" + quote(scope) + ",\"line\":" + line
            + ",\"column\":" + column + ",\"endLine\":" + endLine
            + ",\"endColumn\":" + endColumn + "}";
    }

    private static String quote(String value) {
        StringBuilder result = new StringBuilder("\"");
        for (char character : value.toCharArray()) {
            switch (character) {
                case '\\': result.append("\\\\"); break;
                case '"': result.append("\\\""); break;
                case '\n': result.append("\\n"); break;
                case '\r': result.append("\\r"); break;
                case '\t': result.append("\\t"); break;
                default:
                    if (character < 32) {
                        result.append(String.format("\\u%04x", (int) character));
                    } else {
                        result.append(character);
                    }
            }
        }
        return result.append('"').toString();
    }
}
