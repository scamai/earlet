#if canImport(Testing)
import Testing
@testable import DictationIPC

@Suite("Python subprocess integrity environment")
struct PythonEngineEnvironmentTests {

    @Test("bytecode writes stay disabled despite inherited and caller overrides")
    func bytecodeWritesCannotBeReenabled() {
        let environment = PythonEngineClient.subprocessEnvironment(
            inherited: [
                "KEEP_ME": "inherited",
                "PYTHONUNBUFFERED": "0",
                "PYTHONDONTWRITEBYTECODE": "0",
            ],
            overrides: [
                "KEEP_ME": "overridden",
                "PYTHONUNBUFFERED": "0",
                "PYTHONDONTWRITEBYTECODE": "0",
            ]
        )

        #expect(environment["KEEP_ME"] == "overridden")
        #expect(environment["PYTHONUNBUFFERED"] == "1")
        #expect(environment["PYTHONDONTWRITEBYTECODE"] == "1")
    }
}
#endif
