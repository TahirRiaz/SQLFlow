# pbix-extract: reads a Power BI .pbix semantic model and writes a SQLFlow YAML specification.
#
# Built outside SqlFlow.sln on purpose. The .pbix data model is a compressed binary container, and
# decompressing it means running the vendored XPress9 reference decoder over untrusted input. This
# tool is therefore a standalone executable run offline against files already on disk; nothing here
# is linked into the SQLFlow control plane, so that native code never executes inside the server.

CC ?= cc
PKG_CONFIG ?= pkg-config

VENDOR_DIR := vendor/xpress9
SRC_DIR := src
BUILD_DIR := build

DEPS := libzip expat sqlite3 jansson
DEP_CFLAGS := $(shell $(PKG_CONFIG) --cflags $(DEPS))
DEP_LIBS := $(shell $(PKG_CONFIG) --libs $(DEPS))

# -Wno-unused-parameter and the sign-compare/type-limits relaxations apply to the vendored decoder
# only (see VENDOR_CFLAGS); this project's own sources build clean under the full set.
WARNINGS := -Wall -Wextra -Wpedantic -Wshadow -Wstrict-prototypes -Wmissing-prototypes \
            -Wold-style-definition -Wvla -Wwrite-strings
CFLAGS ?= -O2 -g
# _POSIX_C_SOURCE exposes strdup, which is POSIX rather than ISO C and so is hidden by -std=c11.
CFLAGS += -std=c11 -D_POSIX_C_SOURCE=200809L $(WARNINGS) \
          -I$(SRC_DIR) -I$(VENDOR_DIR)/include $(DEP_CFLAGS)
LDFLAGS += $(DEP_LIBS)

# The vendored decoder is Microsoft's reference implementation, kept byte-for-byte as published so
# it can be diffed against upstream. It does not compile warning-clean under this project's warning
# set, and editing it to do so would fork it; its warnings are suppressed rather than the code
# changed. BUILD_STATIC selects the no-visibility-attribute path in Xpress9Wrapper.h.
VENDOR_CFLAGS := -O2 -g -std=c11 -DBUILD_STATIC -I$(VENDOR_DIR)/include -w

OWN_SOURCES := $(SRC_DIR)/main.c \
               $(SRC_DIR)/buffer.c \
               $(SRC_DIR)/datamodel.c \
               $(SRC_DIR)/metadata.c \
               $(SRC_DIR)/reportlayout.c \
               $(SRC_DIR)/sqlrender.c \
               $(SRC_DIR)/redact.c \
               $(SRC_DIR)/msource.c \
               $(SRC_DIR)/yamlout.c \
               $(SRC_DIR)/xpress9_decode.c

VENDOR_SOURCES := $(VENDOR_DIR)/src/Xpress9DecLz77.c \
                  $(VENDOR_DIR)/src/Xpress9DecHuffman.c \
                  $(VENDOR_DIR)/src/Xpress9Misc.c

OWN_OBJECTS := $(patsubst %.c,$(BUILD_DIR)/%.o,$(OWN_SOURCES))
VENDOR_OBJECTS := $(patsubst %.c,$(BUILD_DIR)/%.o,$(VENDOR_SOURCES))

TARGET := $(BUILD_DIR)/pbix-extract

# The test binary links the reader and renderer directly, without main.o, so the parsers can be
# driven over synthetic .pbix fixtures built in-process.
TEST_DIR := tests
TEST_SOURCES := $(TEST_DIR)/test_reportlayout.c \
                $(TEST_DIR)/test_msource.c
TESTED_SOURCES := $(SRC_DIR)/reportlayout.c \
                  $(SRC_DIR)/sqlrender.c \
                  $(SRC_DIR)/buffer.c \
                  $(SRC_DIR)/datamodel.c \
                  $(SRC_DIR)/metadata.c \
                  $(SRC_DIR)/redact.c \
                  $(SRC_DIR)/msource.c \
                  $(SRC_DIR)/xpress9_decode.c
TEST_TARGET := $(BUILD_DIR)/pbix-extract-tests

# asprintf is a GNU extension these tests use to assemble fixtures; the shipped tool does not.
TEST_CFLAGS := -O1 -g -std=c11 -D_GNU_SOURCE $(WARNINGS) \
               -I$(SRC_DIR) -I$(VENDOR_DIR)/include $(DEP_CFLAGS)

# The parsers run over untrusted report input, so the sanitizers cover exactly the code that most
# needs them. A leak in the expression tree fails the run rather than passing quietly.
SANITIZERS := -fsanitize=address,undefined -fno-omit-frame-pointer

.PHONY: all clean test test-asan

all: $(TARGET)

$(TARGET): $(OWN_OBJECTS) $(VENDOR_OBJECTS)
	@mkdir -p $(dir $@)
	$(CC) $^ -o $@ $(LDFLAGS)

$(BUILD_DIR)/$(SRC_DIR)/%.o: $(SRC_DIR)/%.c
	@mkdir -p $(dir $@)
	$(CC) $(CFLAGS) -c $< -o $@

$(BUILD_DIR)/$(VENDOR_DIR)/src/%.o: $(VENDOR_DIR)/src/%.c
	@mkdir -p $(dir $@)
	$(CC) $(VENDOR_CFLAGS) -c $< -o $@

$(TEST_TARGET): $(TEST_SOURCES) $(TESTED_SOURCES) $(VENDOR_OBJECTS)
	@mkdir -p $(dir $@)
	$(CC) $(TEST_CFLAGS) $(TEST_SOURCES) $(TESTED_SOURCES) $(VENDOR_OBJECTS) -o $@ $(LDFLAGS)

test: $(TEST_TARGET)
	cd $(CURDIR) && ./$(TEST_TARGET)

# Same suite under AddressSanitizer and UndefinedBehaviorSanitizer. The vendored decoder is rebuilt
# with them too, since it is the code handling the least trustworthy bytes in the file.
ASAN_VENDOR_OBJECTS := $(patsubst %.c,$(BUILD_DIR)/asan/%.o,$(VENDOR_SOURCES))

$(BUILD_DIR)/asan/$(VENDOR_DIR)/src/%.o: $(VENDOR_DIR)/src/%.c
	@mkdir -p $(dir $@)
	$(CC) $(VENDOR_CFLAGS) $(SANITIZERS) -c $< -o $@

test-asan: $(TEST_SOURCES) $(TESTED_SOURCES) $(ASAN_VENDOR_OBJECTS)
	@mkdir -p $(BUILD_DIR)
	$(CC) $(TEST_CFLAGS) $(SANITIZERS) $(TEST_SOURCES) $(TESTED_SOURCES) \
	    $(ASAN_VENDOR_OBJECTS) -o $(BUILD_DIR)/pbix-extract-tests-asan $(LDFLAGS)
	cd $(CURDIR) && ./$(BUILD_DIR)/pbix-extract-tests-asan

clean:
	rm -rf $(BUILD_DIR)
